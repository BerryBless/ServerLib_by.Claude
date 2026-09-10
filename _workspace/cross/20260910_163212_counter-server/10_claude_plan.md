# 10_claude_plan — 경합 카운터 서버 구현 계획 (Claude 독립안)

- **작성:** cross-planner (Claude), mode=plan
- **기준 커밋:** `3aa7a80` (워킹트리 깨끗 — 확인함)
- **독립성:** `_workspace/cross/` 내 `*codex*` 파일 미열람. 입력은 `00_context.md` + 프로젝트 코드 + CLAUDE.md 뿐.

---

## 1. 설계 요약 (한 문단)

여러 클라이언트 세션이 **하나의 `long` 필드**를 `IncrementPacket`(Id=3)·`DecrementPacket`(Id=4)로 동시 갱신하는 학습용 예제 서버를 만든다. 핵심 학습 포인트는 **"같은 부하에서 `Interlocked`는 결정적 결과를, 비원자 `_v = _v + 1`은 유실된 갱신을 낸다"** 이므로, 카운터 갱신 전략을 토글(`SharedCounter.UseInterlocked`)로 노출한다. 검증은 신규 `CounterGetPacket`(Id=18) → `CounterValuePacket`(Id=19) 요청/응답과 **2단계 배리어 프로토콜**로 수행해 다중 클라이언트 환경에서도 결정적 기대값을 단언할 수 있게 한다.

---

## 2. 코드 근거로 확정한 사실 (설계의 전제)

이 계획의 비자명한 결정들은 전부 아래 실측 사실 위에 서 있다. 구현자·리뷰어는 이 4개를 먼저 확인할 것.

| # | 사실 | 근거 (파일:라인) | 설계에 미치는 영향 |
|---|------|-----------------|-------------------|
| F1 | **`Deserialize<T>`는 와이어 헤더의 PacketId가 `T`와 일치하는지 검증하지 않는다.** 헤더를 건너뛰고 무조건 `new T()`를 만들어 본문을 읽는다. | `ServerLib/Core/Serialization/BinaryPacketSerializer.cs:59-71` | **라우팅은 반드시 `PacketPool.TryParseHeader`로 얻은 `packetId`로 분기해야 한다.** `Deserialize<T>` 결과로 타입을 판별하려 들면 오라우팅이 조용히 쓰레기 값을 만든다. |
| F2 | **세션당 패킷 디스패치는 엄격히 순차적이다.** 프레이밍 루프가 `await DispatchPacketAsync(...)`로 이전 콜백 완료를 기다린 뒤 다음 패킷을 처리한다. | `ServerLib/Core/Transport/SocketPipelineSession.cs:197` | **2단계 배리어 프로토콜이 성립한다.** 한 연결에서 ops를 모두 보낸 뒤 `CounterGet`을 보내고 응답을 받으면, 그 세션의 ops는 전부 적용 완료가 보장된다. |
| F3 | **`OnReceived`에서 예외가 새어나가면 해당 세션이 종료된다.** `OnReceiveError` 통지 후 `return` → `finally`에서 `OnDisconnected` + 정리. | `SocketPipelineSession.cs:199-215` | 손상·미지 패킷을 예외로 처리하면 세션이 죽는다. 학습 데모에서는 **드롭 후 세션 유지**가 낫다 → 핸들러 내부에서 방어적으로 처리(§6). |
| F4 | `SpanReader`는 경계 초과 시 `EndOfStreamException`을 던진다. `ISession.SendAsync`는 Thread-safe로 문서화되어 있다. | `SpanReader.cs:48-53`, `ISession.cs:185-194` | 본문 파싱 실패의 예외 타입이 확정됨. 세션 응답 송신에 별도 직렬화 락 불필요. |
| F5 | **클라이언트 수신 루프도 패킷당 `await OnReceived(...)`로 순차 호출한다.** 연결당 읽기 루프는 1개. | `ServerLib/Core/Transport/SocketPipelineClient.cs:206,216` | 응답 `Channel`에 **`SingleWriter=true`를 켜도 안전**하다(§6-C). 생산자가 실제로 1개임이 확인됨 — 이 전제가 틀리면 `SingleWriter=true`는 예외가 아니라 **조용한 손상**을 낸다. |
| F6 | 예약 ID `0xFFFE`(PING)/`0xFFFF`(PONG)는 `DispatchPacketAsync`가 가로채 앱 `OnReceived`를 **호출하지 않는다**. | `SocketPipelineSession.cs:271`, `SocketPipelineClient.cs:194` | "미지 패킷 드롭" 테스트(E5)에 이 두 ID를 쓰면 안 된다 — 핸들러에 도달조차 하지 않아 의도한 경로를 검증하지 못한다. |

---

## 3. 설계 결정과 대안 비교

### D1. 기존 `IncrementPacket`/`DecrementPacket` 재사용 — **채택**

| 후보 | 평가 |
|------|------|
| **(채택) Id=3/4 재사용** | 요구사항 원문("더하기 패킷, 빼기 패킷")과 정확히 일치. 두 패킷의 XML 주석이 문자 그대로 *"서버의 test 변수를 1 증감"* 이며 현재 소비자가 없다 — 이 기능이 그 주석의 실현이다. ServerLib 무변경. |
| 수량 필드(`Amount`) 있는 신규 패킷 | 요구사항에 없음. 경합의 세기는 `Amount` 크기가 아니라 **동시 호출 횟수**가 결정하므로 학습 가치도 없다. "간단하게/작게" 지시 위반(scope creep). |

### D2. 신규 패킷 위치 = **`ServerLib/Core/Serialization/Packets/`**

"데모 전용 패킷을 배포 라이브러리에 넣지 말라"는 반론이 가능하나, **현행 리포 관례가 명확히 반대**다: `MobHpPacket`(6)·`MobDeathPacket`(7)·`TicketReserveRequestPacket`(13)·`SeatMapResponsePacket`(17) 등 순수 게임/데모 콘텐츠 패킷이 전부 이 디렉토리에 있다. 관례 일관성을 따른다. (예제 프로젝트 로컬에 `IPacket` 구현을 두는 것도 기술적으로 가능하나 — `IPacket`·`SpanWriter`·`SpanReader`·`PacketPool` 모두 public — 관례 이탈 비용이 이득보다 크다.)

### D3. 검증 경로 = **`CounterGetPacket` 요청/응답 (op별 ack 없음)**

| 후보 | 평가 |
|------|------|
| **(채택) 조회 요청/응답 2종 신규** | Increment/Decrement는 단방향(fire-and-forget)으로 유지 → 핫 패스가 순수 경합만 담게 되어 데모 의도에 부합. 조회 시점만 왕복. |
| op마다 현재값 ack 회신 | 와이어 트래픽 2배. 게다가 **최종 합계 확인에는 여전히 별도 조회가 필요**하므로 문제를 해결하지 못한다. |
| `StatsRequestPacket`(8)/`StatsResponsePacket`(9) 재사용 | 의미 충돌. `StatsResponsePacket`은 **UTF-8 JSON 문자열** 본문의 관리용 패킷(`StatsResponsePacket.cs:53,69`)이라 카운터 값 운반에 부적합하고, 매 조회마다 string 할당이 발생한다. |

### D4. `AppliedOps`(처리 패킷 수)를 응답에 포함 — **채택, 단 항상 Interlocked**

`Value`만으로는 "경합으로 갱신이 유실됐다"와 "패킷이 애초에 도착하지 않았다"를 구분할 수 없다. `AppliedOps`는 **측정 계기**이므로 `UseInterlocked` 토글과 무관하게 **항상 `Interlocked.Increment`** 를 쓴다. 그 결과 unsafe 모드에서 다음 대비가 만들어지며, 이것이 이 예제의 교육적 핵심이다:

```
[safe]   Value = 1600  (= 기대값)   AppliedOps = 6400  (= 총 변경 op 수)
[unsafe] Value = 1417  (≠ 기대값)   AppliedOps = 6400  (= 총 변경 op 수)  ← 패킷은 다 왔다. 갱신만 유실됐다.
```

**⚠ `AppliedOps`의 계수 범위(구현자 필독):** `AppliedOps`는 **변경 op(`IncrementPacket`·`DecrementPacket`)만** 계수한다. **조회(`CounterGetPacket`)는 포함하지 않으며**, 미지·손상 패킷도 포함하지 않는다(그건 `_unknownPackets`/`_malformedPackets`가 따로 센다). 즉 증가 지점은 `SharedCounter.Add()`/`Subtract()` **내부 뿐**이고, `CounterServerHost`의 디스패치 루프에서 패킷마다 증가시키면 **안 된다.** 각 클라가 조회를 1~2회 보내므로 그렇게 구현하면 E4가 클라 수만큼 어긋나 "패킷 중복"처럼 보이는 오진을 낳는다.

**쌍의 원자성 주의:** `Value`와 `AppliedOps`는 각각 원자적이지만 **둘을 함께 읽는 것은 원자적이지 않다.** 따라서 **정지(quiesced) 상태의 조회에서만 쌍이 의미를 갖는다.** 이를 코드로 강제하기 위해:
- 서버 콘솔의 주기 출력은 **`Value`만** 찍는다.
- `(Value, AppliedOps)` 쌍은 **최종 정지 조회 결과에서만** 출력·단언한다.
- `CounterValuePacket` XML `<remarks>`에 이 제약을 명시한다.

### D5. unsafe 모드는 **경합 창을 인위적으로 확대**한다 (데모 전용)

루프백 + 짧은 임계구간에서는 `_v = _v + 1`이 유실을 안 낼 수도 있어 학습자가 "경합은 없다"는 반대 결론을 얻을 위험이 있다. 따라서 unsafe 경로에 한해 읽기와 쓰기 사이에 `Thread.SpinWait(_raceWindowSpins)`를 넣어 창을 넓힌다. **이 확대는 교육용이며 실제 프로덕션 코드의 재현이 아님**을 인라인 주석으로 명시한다.

**단, 어떤 테스트도 unsafe 모드의 값 유실을 단언하지 않는다** (레이스는 확률적 → flaky CI). 테스트는 unsafe 모드에 대해 `AppliedOps == 총 ops`만 단언한다(이건 항상 참).

### D6. 신규 테스트 프로젝트 `CounterExample.Tests` — **채택** (기존 `EchoExample.Tests` 확장 아님)

근거: 테스트가 예제 코드(`CounterServer`/`CounterClient`)를 **실제로 실행**해야 하므로 `ProjectReference`가 필요한데, 이를 `EchoExample.Tests`에 추가하면 에코 테스트 프로젝트가 무관한 카운터 예제에 결합된다(`EchoExample.Tests.csproj`는 현재 ServerLib 단일 참조이고 주석에 *"public API만 사용"* 을 명시하고 있다). 실패 도메인도 분리된다. `dotnet test ClaudeCodeStudy.sln`은 그대로 양쪽을 모두 실행한다.

### D7. 로직을 `Program.cs`가 아닌 클래스로 분리 — **채택**

`EchoExample.Tests`는 *"Program.cs는 top-level 문이라 테스트에서 직접 호출할 수 없다"* 며 테스트가 와이어링을 **복제**한다(`EchoEndToEndTests.cs:22-24`). 에코는 1줄이라 감당되지만 카운터 핸들러는 라우팅+분기+응답이라 복제 시 "테스트는 통과하는데 예제는 틀린" 상태가 생긴다. → 재사용 가능한 클래스로 빼고 `Program.cs`와 테스트가 **동일 코드**를 쓴다.

### D8. 포트 = **9010**

9000=EchoServer, 8080=EchoWeb 사용 중. 9010은 미사용. 테스트는 포트를 고정하지 않고 `GetFreePort()`(EchoEndToEndTests 패턴)로 확보한다.

---

## 4. 변경 파일 목록

### 4-1. 신규 — ServerLib (패킷 2종)

| 파일 | 요지 |
|------|------|
| `ServerLib/Core/Serialization/Packets/CounterGetPacket.cs` | **신규.** `struct`, `Id = 18`, 본문 0B. 현재 카운터 값 조회 요청. `IncrementPacket`과 동일한 무본문 struct 패턴(역직렬화 무할당). |
| `ServerLib/Core/Serialization/Packets/CounterValuePacket.cs` | **신규.** `struct`, `Id = 19`, 본문 **16B** = `long Value`(8B LE) + `long AppliedOps`(8B LE). `Serialize`는 `WriteInt64` 2회, `Deserialize`는 `ReadInt64` 2회. `DamagePacket.cs`의 고정 본문 struct 패턴을 따른다. XML `<remarks>`에 **D4의 쌍 비원자성 제약**을 명시. |

> **Id 충돌 확인:** 현재 사용 중 = 1~17, `0xFFFE`(PING), `0xFFFF`(PONG). 18·19는 미사용 (`grep "public const ushort Id" ServerLib/` 전수 확인함).

**ServerLib 기타 변경 없음.** Transport·Interface·직렬화 코어는 손대지 않는다.

### 4-2. 신규 — `CounterServer/` (실행 예제)

| 파일 | 요지 |
|------|------|
| `CounterServer/CounterServer.csproj` | `Exe`, `net10.0`, `Nullable`/`ImplicitUsings` enable, `RootNamespace=CounterServer`, `ProjectReference → ServerLib`. `EchoServer.csproj` 그대로 복제. |
| `CounterServer/SharedCounter.cs` | **경합의 대상.** `private long _value; private long _appliedOps;` (필드여야 함 — `Interlocked`는 `ref`를 요구하므로 프로퍼티 불가). `Add()`/`Subtract()`/`Read()`/`ReadAppliedOps()`. `UseInterlocked=true`면 `Interlocked.Add(ref _value, ±1)`, false면 `읽기 → Thread.SpinWait(창확대) → 쓰기`의 비원자 경로. `_appliedOps`는 **모드 무관 항상 `Interlocked.Increment`**(D4). 소켓 무관 순수 클래스 → 단위 테스트 가능. |
| `CounterServer/CounterServerHost.cs` | **와이어링 재사용 단위.** `Start(int port, IPAddress bind)` / `Stop()`. `ServerNet.CreateListener()` → `OnReceived`에서 **`PacketPool.TryParseHeader`로 얻은 id를 `switch`** (F1) → 3=Add, 4=Subtract, 18=조회 응답, 그 외=드롭+카운트. `SharedCounter`·드롭 카운터를 공개 프로퍼티로 노출. `Program.cs`와 테스트가 공유. |
| `CounterServer/Program.cs` | 콘솔 호스트. 상단에 토글 상수(`UseInterlocked`, `RaceWindowSpins`, `Port=9010`) + `EchoServer/Program.cs` 수준의 학습용 주석 블록. `CounterServerHost` 기동 → 500ms 주기 `[COUNTER] value=...` 출력(**Value만** — D4) → 아무 키 종료 시 최종 `(Value, AppliedOps)` 쌍 요약 출력. |

### 4-3. 신규 — `CounterClient/` (부하 생성 예제)

| 파일 | 요지 |
|------|------|
| `CounterClient/CounterClient.csproj` | `EchoClient.csproj` 복제 + `ProjectReference → ServerLib`. |
| `CounterClient/CounterLoadClient.cs` | **가상 클라이언트 1개 = 연결 1개.** `ServerNet.CreateClient()` → `OnReceived`에서 헤더 id 확인 후 `CounterValuePacket`만 `Channel.Writer`에 기록 → `RunOpsAsync(int adds, int subs)` (증감 인터리브 순차 `await` 송신) → `QueryAsync()` (CounterGet 송신 후 `Channel.Reader.ReadAsync` 대기). `OnDisconnected`에서 `Writer.TryComplete(예외)` → **대기 중인 조회가 타임아웃까지 매달리지 않고 즉시 명확한 실패**(§6-C). |
| `CounterClient/Program.cs` | 토글 상수(`ClientCount=8`, `AddsPerClient=500`, `SubsPerClient=300`, `Port=9010`) + 주석 블록. K개 `CounterLoadClient`를 `Task.WhenAll`로 동시 구동 → **2단계 배리어**(§5) → 최종 조회 → `기대값 vs 실측값 vs AppliedOps` 비교표 출력 + 일치/불일치 판정. |

### 4-4. 신규 — `CounterExample.Tests/`

| 파일 | 요지 |
|------|------|
| `CounterExample.Tests/CounterExample.Tests.csproj` | `EchoExample.Tests.csproj` 복제(xunit 2.9.3 / Test.Sdk 17.12.0 버전 일치) + `ProjectReference → ServerLib, CounterServer, CounterClient`. |
| `CounterExample.Tests/SharedCounterTests.cs` | 소켓 없는 순수 동시성 단위 테스트. |
| `CounterExample.Tests/CounterPacketRoundTripTests.cs` | 신규 패킷 2종 직렬화 왕복 + Id 상수 회귀 고정. |
| `CounterExample.Tests/CounterEndToEndTests.cs` | 루프백 실소켓 E2E. `GetFreePort()`·`WaitAsync` 타임아웃 패턴은 `EchoEndToEndTests.cs`를 따른다. |

### 4-5. 수정

| 파일 | 요지 |
|------|------|
| `ClaudeCodeStudy.sln` | `CounterServer`·`CounterClient`·`CounterExample.Tests` 3개 등록. |
| `CLAUDE.md` | "예제 코드 위치" 목록에 `CounterServer/Program.cs`·`CounterClient/Program.cs` 항목 1줄씩 추가(프로젝트 규칙: 새 기능 추가 시 예제 갱신). |
| `plan/counter_contention_0910.md` | **신규.** CLAUDE.md 플랜 문서화 규칙에 따른 설계 문서 + 문서 목록 표에 행 추가. |

---

## 5. 공개 API 영향

**호환성 파괴 없음(순수 추가).**

- 신규 public 타입 2개: `ServerLib.Core.Serialization.Packets.CounterGetPacket`, `CounterValuePacket`. 기존 타입 시그니처 변경 0건.
- **패킷 Id 18·19가 영구 예약된다.** 향후 신규 패킷은 20부터. (테스트가 Id 상수를 고정 단언해 무단 재배치를 차단한다.)
- `IServerListener`/`ISession`/`IClientConnection`/`ServerNet` **변경 없음.** 예제는 `ServerNet` 팩토리 반환 인터페이스만 사용하며 internal Transport 타입에 접근하지 않는다(캡슐화 규칙 준수).
- `CounterServer`/`CounterClient`의 public 타입(`SharedCounter`·`CounterServerHost`·`CounterLoadClient`)은 예제 실행 파일 소속이며 NuGet 배포 대상(`pack.ps1`)이 아니다.

### 와이어 프로토콜 (전부 기존 4B 헤더 `[Id(2)|BodyLength(2)]` LE 준수)

| 방향 | 패킷 | Id | 본문 |
|------|------|----|------|
| C→S | `IncrementPacket` | 3 | 0B (단방향, 응답 없음) |
| C→S | `DecrementPacket` | 4 | 0B (단방향, 응답 없음) |
| C→S | `CounterGetPacket` | 18 | 0B |
| S→C | `CounterValuePacket` | 19 | 16B = `Value`(int64 LE) + `AppliedOps`(int64 LE) |

### 2단계 배리어 검증 프로토콜 (F2에 의존)

```
Phase 1 (병렬):  각 클라 c: [Inc×N, Dec×M 인터리브 순차송신] → CounterGet → 응답 대기
                 └ F2(세션당 순차 디스패치)에 의해 응답 수신 = 그 세션의 N+M ops 적용 완료
Barrier:         모든 클라의 Phase 1 응답 수신 (Task.WhenAll) = 전체 ops 적용 완료 = 정지 상태
Phase 2 (단일):  임의의 클라 1개가 CounterGet → 응답
Assert:          Value == K×(N−M)   &&   AppliedOps == K×(N+M)
```

**Phase 1 응답값 자체는 비결정적이다**(다른 세션 ops가 진행 중). 배리어 신호로만 쓰고 값은 단언하지 않는다 — 계획·구현·테스트 모두에서 이 점을 주석으로 못박는다.

---

## 6. 동시성 · 메모리 정책 (CLAUDE.md 주석 규칙 대상)

### A. 카운터 상태 — `SharedCounter`

- **`private long _value;` (필드, 비-readonly, 비-프로퍼티)**
  인라인 주석 근거: `Interlocked.*`는 `ref long`을 요구하므로 프로퍼티는 문법적으로 불가. `Interlocked.Add(ref long, long)`은 x64에서 `lock xadd` 단일 명령으로 컴파일되어 읽기-수정-쓰기 전체를 캐시라인 잠금(MESI) 수준에서 원자화한다 → 다중 코어가 같은 이전 값을 읽는 lost update가 구조적으로 불가능.
- **읽기는 `Interlocked.Read(ref _value)`**
  근거: 64비트 런타임에서 정렬된 `long` 로드는 이미 원자적이지만, `Interlocked.Read`는 32비트 포함 모든 플랫폼에서 찢김(torn read)을 배제하며 **의도를 코드로 표현**한다. 단순 `_value` 읽기는 JIT가 레지스터에 고정(enregister)할 여지를 남긴다.
- **unsafe 경로**: `long v = _value; Thread.SpinWait(n); _value = v + delta;`
  인라인 주석 근거: 이 3단계는 원자 단위가 아니므로 두 코어가 동일한 `v`를 읽고 각자 `v+1`을 써서 **증가 1회가 소실**된다. `Thread.SpinWait`는 `PAUSE` 명령을 방출해 커널 전환 없이 창만 넓히는 교육용 장치이며 실제 코드 패턴이 아님을 명시.
  - **`RaceWindowSpins` 상한 — 수십 단위(기본 `32`)로 둘 것. 수천은 금지.** 근거: 이 스핀은 E2E 경로에서 **IO 스레드의 `OnReceived` 내부**에서 돌고, F2에 의해 해당 세션의 읽기 루프 전체가 그동안 **직렬로 정지**한다. 값을 크게 잡으면 유실이 잘 보이는 대신 데모가 멈춘 것처럼 보이고 E2E 테스트가 타임아웃에 걸린다. 32 스핀이면 다중 코어에서 유실을 관찰하기에 충분하다.
- **`_appliedOps`는 모드 무관 항상 `Interlocked.Increment`** (D4: 측정 계기이므로 관측 대상과 분리).
- **전통 락 0개.** `lock`/`Monitor`/`SemaphoreSlim` 미사용 → 정당화 주석 대상 없음(CLAUDE.md 동시성 규칙 충족).
- **오버플로:** `long`이며 데모 규모(≤10⁵)에서 도달 불가. `Interlocked`는 오버플로 시 조용히 랩어라운드함을 XML에 1줄 명시(비범위).
- **False sharing:** 두 필드가 같은 캐시라인에 놓여 코어 간 무효화가 오갈 수 있음을 주석으로 언급하되, 데모 규모에서 패딩은 하지 않는다(과잉 설계 회피).

### B. 서버 수신 경로 — `CounterServerHost.OnReceived`

- **Thread Context:** IO 스레드 풀에서 호출. 세션 간에는 **동시**, 세션 내부는 **순차**(F2). 이 사실을 콜백 XML `<remarks>`에 명시 — 배리어 프로토콜의 근거이기 때문.
- **핸들러 전체가 동기 완료 경로.** 블로킹 호출(파일/DB/`.Result`/`.Wait()`) 0건. Increment/Decrement 처리는 `Interlocked` 1회 후 `ValueTask.CompletedTask` 반환(캐시드 인스턴스, 무할당).
- **`data` 소유권:** `ReadOnlyMemory<byte>`는 Pipe 세그먼트의 얕은 뷰 → **콜백 반환 후 무효**. 헤더 파싱을 `data.Span`으로 동기 수행하고 어떤 참조도 캡처·보관하지 않는다.
- **할당:** 조회 응답만 `session.SendAsync<CounterValuePacket>` (확장 메서드가 `ArrayPool<byte>.Shared.Rent`로 버퍼 대여 후 반납 — `PacketSendExtensions.cs:36-53`). `CounterValuePacket`이 struct라 직렬화 경로도 무할당. 증감 패킷은 응답이 없으므로 **완전 무할당**.
- **`switch` 라우팅은 헤더 id 기준** (F1). 세 패킷 모두 본문 0B이므로 `Deserialize<T>` 호출조차 불필요 — `bodyLength == 0` 검증만 한다(파싱 예외 자체가 발생하지 않는 경로).

### C. 클라이언트 — `CounterLoadClient`

- **`Channel<CounterValuePacket>` (Unbounded, `SingleReader=true, SingleWriter=true, AllowSynchronousContinuations=false`)**
  인라인 주석 근거: 조회 응답은 IO 스레드(단일 생산자) → 드라이버 태스크(단일 소비자)의 SPSC 경로다. **생산자가 실제로 1개임은 F5로 확인됨** — `SocketPipelineClient`의 읽기 루프는 연결당 1개이고 패킷마다 `await OnReceived(...)`로 순차 호출한다. (이 전제가 틀리면 `SingleWriter=true`는 예외 대신 조용한 큐 손상을 낸다. 향후 클라 수신 경로가 병렬화되면 `SingleWriter=false`로 내려야 한다.) `SingleReader/SingleWriter`를 켜면 Channel이 락-프리 전용 구현으로 특수화되어 큐 연산에 락 경합이 없다. `AllowSynchronousContinuations=false`는 `ReadAsync` 대기자의 연속(continuation)이 `TryWrite`를 호출한 **IO 스레드에서 인라인 실행되는 것을 막아** 수신 루프 점유와 재진입 위험을 제거한다.
  - Unbounded 선택 근거: 응답은 조회당 정확히 1개로 상한이 명확 → 백프레셔 불필요. Bounded의 `WriteAsync` 대기가 IO 스레드를 붙잡는 위험이 오히려 크다.
- **연결 1개 = 가상 클라 1개, 송신은 순차 `await`.** 한 연결을 여러 스레드가 공유하지 않는다. 이유: 경합을 **서버 측 공유 상태 한 곳**으로 국한해야 데모의 인과가 명확해진다. (`ISession.SendAsync`는 Thread-safe로 문서화(F4)돼 있지만 순서 보장은 OS 소켓 버퍼에 달려 있어 굳이 기댈 이유가 없다.)
- **연결 끊김 즉시 실패:** `OnDisconnected`에서 `channel.Writer.TryComplete(new IOException(...))` → 대기 중인 `ReadAsync`가 즉시 예외. 이게 없으면 응답 유실이 **타임아웃까지 매달렸다가 원인 불명의 `TimeoutException`** 으로 표면화된다.

---

## 7. 예외 · 실패 경로 처리

| 상황 | 처리 | 근거 |
|------|------|------|
| **미지 패킷 Id** (예: 클라가 `ChatPacket`(2) 송신) | 드롭 + `Interlocked.Increment(ref _unknownPackets)`. **예외 던지지 않음, 세션 유지.** | F3 — 예외가 새면 세션이 종료된다. 학습 데모에서는 나쁜 패킷 하나로 연결이 끊기면 원인 파악이 어렵다. 라이브러리 기본 동작(세션 종료)이 프로덕션에서는 옳은 선택임을 주석으로 병기. |
| **본문 길이 불일치** (Id는 3/4/18인데 `bodyLength != 0`) | 드롭 + `_malformedPackets` 증가. 세션 유지. | 위와 동일. 사전 검증이므로 `EndOfStreamException`(F4)이 발생할 경로 자체를 만들지 않는다. |
| `TryParseHeader` 실패 (`< 4B`) | 프레이밍(F2)이 완전 패킷을 보장하므로 도달 불가. 방어적 드롭 + `_malformedPackets`. | 방어적 코딩. |
| **조회 응답 송신 실패** (`ObjectDisposedException`/`SocketException` — 조회 직후 세션이 죽은 경우) | `try/catch`로 삼킴. 이미 사라진 상대에게 못 보낸 응답 때문에 세션을 죽일 이유가 없다. | F3. |
| **클라 `ConnectAsync` 실패** | `SocketException` 전파. `Program.cs`는 "서버(9010)를 먼저 실행하세요" 안내 출력 후 종료. | — |
| **클라 송신 중 연결 끊김** | `SendAsync` 예외를 `CounterLoadClient`가 잡아 부분 진행량과 함께 보고. 테스트에서는 실패로 처리. | — |
| **테스트 무한 대기** | 모든 비동기 대기에 `.WaitAsync(TimeSpan)` 적용. E2E 타임아웃 5s(단일)·30s(다중 클라 부하). | `EchoEndToEndTests.cs:39,137` 패턴. |
| **리스너 정리** | 모든 테스트가 `try/finally`에서 `listener.Stop()`(호스트의 `Stop()`) 호출. 클라는 `await using`. | `EchoEndToEndTests.cs:140-144` 패턴. |

---

## 8. 테스트 전략

### 8-1. `SharedCounterTests.cs` — 순수 단위 (소켓 없음, 빠름)

| # | 테스트 | 단언 |
|---|--------|------|
| U1 | `Read_OnNewCounter_ReturnsZero` | `Value==0 && AppliedOps==0` |
| U2 | `AddAndSubtract_SingleThreaded_ProducesExpectedValue` | 순차 100 add / 40 sub → `Value==60`, `AppliedOps==140` |
| U3 | `ConcurrentAddSubtract_WithInterlocked_IsDeterministic` | **핵심.** 8 태스크 × (10,000 add + 10,000 sub) 동시 → `Value==0`, `AppliedOps==160,000`. `Interlocked` 정확성 증명. |
| U4 | `ConcurrentAdd_WithInterlocked_MatchesTotal` | 8 태스크 × 10,000 add → `Value==80,000` (감산 없는 단방향 케이스) |
| U5 | `AppliedOps_IsExact_EvenWhenValueRaceIsEnabled` | `UseInterlocked=false` + **소규모** 동시 부하 → **`AppliedOps`만** 정확히 단언. `Value`는 **단언하지 않음**(D5: flaky 금지). 계기와 관측 대상의 분리를 실증. **규모: 4 태스크 × 1,000 op** — `AppliedOps`만 보므로 큰 규모가 불필요하고, 경합 창(`SpinWait`)이 켜진 상태라 U3의 160,000 규모를 복사하면 단위 테스트가 느려진다. |

### 8-2. `CounterPacketRoundTripTests.cs` — 직렬화

| # | 테스트 | 단언 |
|---|--------|------|
| P1 | `CounterValuePacket_RoundTrip_PreservesFields` `[Theory]` — `(0,0)`, `(1600,6400)`, `(-500,500)`, `(long.MaxValue, long.MinValue)` | Serialize→Deserialize 후 두 필드 보존. 음수·경계값 포함. |
| P2 | `CounterValuePacket_GetBodySize_Is16` | `==16`, 직렬화 총 길이 `==20`(헤더 4 + 본문 16) |
| P3 | `CounterGetPacket_GetBodySize_IsZero` | `==0`, 총 길이 `==4` |
| P4 | `PacketIds_AreStable` `[Theory]` — Increment=3, Decrement=4, CounterGet=18, CounterValue=19 | **회귀 고정.** 향후 Id 재배치가 예제 프로토콜을 조용히 깨는 것을 차단(D2·§5). |

### 8-3. `CounterEndToEndTests.cs` — 루프백 실소켓

`GetFreePort()`(테스트별 독립 포트) + `IPAddress.Loopback` 바인딩 + `try/finally` 정리. **실제 `CounterServerHost`·`CounterLoadClient` 코드를 구동**한다(D7).

| # | 테스트 | 단언 |
|---|--------|------|
| E1 | `Query_OnFreshServer_ReturnsZero` | 연결 직후 `CounterGet` → `Value==0, AppliedOps==0`. 요청/응답 경로 최소 검증. |
| E2 | `SingleClient_AddsAndSubtracts_FinalValueIsDeterministic` | 1클라 500 add / 300 sub → `Value==200`, `AppliedOps==800`. |
| E3 | `ConcurrentClients_FinalValueIsDeterministic` | **핵심 경합 테스트.** 8클라 × (500 add + 300 sub) 동시 + 2단계 배리어 → `Value == 8×200 == 1600`. |
| E4 | `ConcurrentClients_AppliedOpsEqualsTotalSent` | 동일 부하에서 `AppliedOps == 8×800 == 6400` — **패킷 유실 0** 확인 (E3의 결정성이 "우연한 상쇄"가 아님을 보강). |
| E5 | `UnknownPacketId_IsDroppedAndSessionSurvives` | **미할당 Id=250**의 4B 프레임(`[250(2B LE)][0(2B LE)]`)을 수동 조립해 `IClientConnection.SendAsync(ReadOnlyMemory<byte>)`로 raw 송신(E6와 동일 경로) → 이어서 `CounterGet` 송신 → **응답이 정상 도착**하고 `Value`가 변하지 않음. F3 대응 설계(§7)의 실증.<br/>**Id 선택 주의:** ① `ChatPacket`(2) 등 *할당된* Id는 "미지 패킷"이 아니라 "미처리 기지 패킷"이라 검증 강도가 약하고, 훗날 그 패킷을 쓰는 예제가 생기면 전제가 무너진다. ② `0xFFFE`/`0xFFFF`는 **F6에 의해 `OnReceived`에 도달조차 하지 않으므로** 쓰면 안 된다. |
| E6 | `MalformedBodyLength_IsDroppedAndSessionSurvives` | Id=3인데 본문 4B를 실은 프레임을 **수동 조립**해 `IClientConnection.SendAsync(ReadOnlyMemory<byte>)`로 raw 송신 → 드롭되고 `Value` 불변, 후속 `CounterGet` 정상 응답. |

**테스트 수 (xUnit 발견 기준 = `[Theory]` 케이스 전개 후):**
신규 = U1~U5(5) + P1(Theory 4케이스) + P2(1) + P3(1) + P4(Theory 4케이스) + E1~E6(6) = **21개**.
기존 14개(`EchoEndToEndTests` 6 + `EchoPacketRoundTripTests` 8, 역시 케이스 전개 기준)는 **무수정 유지** → 합계 **35개**.

> 이 숫자는 `[Theory]` 케이스 수를 어떻게 세느냐에 따라 달라지므로 **검증 게이트로는 절대값 대신 "기존 14개 전원 유지 + 신규 전량 통과 + 실패 0"** 를 쓴다(§9-10).

### 8-4. 실행 명령

```bash
# 전체 빌드
dotnet build ClaudeCodeStudy.sln

# 전체 테스트 (기존 14 유지 + 신규 21 = 35, Theory 케이스 전개 기준)
dotnet test ClaudeCodeStudy.sln

# 카운터만 (빠른 반복)
dotnet test CounterExample.Tests/CounterExample.Tests.csproj
dotnet test CounterExample.Tests/CounterExample.Tests.csproj --filter "FullyQualifiedName~CounterEndToEndTests"

# 수동 데모 (터미널 2개)
dotnet run --project CounterServer      # 9010에서 대기
dotnet run --project CounterClient      # 8클라 동시 부하 → 검증 리포트

# 경합 실패 시연: CounterServer/Program.cs의 UseInterlocked = false 로 바꾼 뒤 재실행
```

---

## 9. 구현 순서

각 단계는 이전 단계가 빌드/테스트 통과한 뒤 진행한다.

1. **패킷 2종 추가** — `CounterGetPacket`(18)·`CounterValuePacket`(19) 작성. XML 문서 + struct 선택 근거 인라인 주석. → `dotnet build ServerLib/ServerLib.csproj`
2. **`CounterServer` 프로젝트 생성** — csproj + `SharedCounter.cs`. 이 시점에서 `CounterServerHost`는 아직 없음.
3. **`CounterExample.Tests` 프로젝트 생성 + 단위/직렬화 테스트** — U1~U5, P1~P4 작성·통과 확인. **네트워크 코드 이전에 경합 로직의 정확성을 먼저 고정**한다. → `dotnet test CounterExample.Tests/...`
4. **`CounterServerHost.cs` 작성** — 헤더 id `switch` 라우팅(F1), 드롭 정책(§7), 응답 송신.
5. **`CounterClient` 프로젝트 + `CounterLoadClient.cs`** — Channel 수신 펌프, `RunOpsAsync`/`QueryAsync`, `OnDisconnected` fast-fail.
6. **E2E 테스트 작성** — E1 → E2 → E3/E4 → E5/E6 순서로 하나씩 통과시킨다. E3에서 결정성이 깨지면 배리어 프로토콜(F2 전제)부터 재점검.
7. **`Program.cs` 2종 작성** — 토글 상수·학습용 주석 블록·콘솔 출력. `EchoServer`/`EchoClient` 수준의 주석 밀도 유지.
8. **솔루션 등록 + 검증**
   ```bash
   dotnet sln ClaudeCodeStudy.sln add CounterServer/CounterServer.csproj CounterClient/CounterClient.csproj CounterExample.Tests/CounterExample.Tests.csproj
   ```
   **⚠ 필수 확인:** 이 솔루션의 `GlobalSection(ProjectConfigurationPlatforms)`는 프로젝트당 **12행**(Debug/Release × AnyCPU/x64/x86 × ActiveCfg/Build.0)을 갖는다. `dotnet sln add`가 `Any CPU` 행만 기록했다면 기존 항목(예: `{24BB454D-...}`)의 모양을 그대로 복제해 x64/x86 행을 **수동 추가**할 것. 누락 시 x64/x86 구성에서 새 프로젝트가 빌드되지 않는다.
9. **문서화** — `plan/counter_contention_0910.md` 작성(CLAUDE.md 플랜 문서 7개 필수 항목) + 플랜 목록 표 행 추가 + CLAUDE.md 예제 목록에 2줄 추가.
10. **최종 검증** — `dotnet build ClaudeCodeStudy.sln` 0 오류(경고 신규 0건), `dotnet test ClaudeCodeStudy.sln` **실패 0 + 기존 14개 전원 유지 + 신규 전량 통과**(절대 개수 대신 이 조건을 게이트로 쓴다 — §8-3 각주), 수동 데모 2종(safe/unsafe) 실행 확인.

---

## 10. 비범위 (Non-goals)

- **성능 측정·벤치마크 하네스 없음.** 처리량/지연 수치를 내지 않는다. 목적은 정확성 시연이다.
- **인증·모니터링·웹 UI·티켓팅 연동 없음.** `ISessionRegistry`·브로드캐스트·`[STATS]` JSON 포트 미사용.
- **신규 Transport·프로토콜 확장 없음.** 기존 4B 헤더와 `ServerNet` 팩토리 public API만 사용. `ServerLib/Interface`·`Core/Transport`·직렬화 코어 **무변경**.
- **수량 필드(±N) 없음.** ±1 고정(D1).
- **unsafe 모드의 값 유실을 단언하는 테스트 없음** (D5 — flaky 회피). 콘솔 데모로만 관찰.
- **`lock`/`SemaphoreSlim` 기반 대안 구현 비교 없음.** Interlocked vs 비원자 2종으로 제한(과잉 설계 회피).
- **오버플로 방어·값 상한·속도 제한 없음.** 루프백 학습 예제이며 신뢰 경계 밖 입력을 가정하지 않는다(다만 미지·손상 패킷 드롭은 §7에서 처리).
- **`EchoServer`/`EchoClient`/`EchoWeb`/`EchoExample.Tests` 무수정.**

---

## 11. [미해결 질문]

| # | 질문 | 기본값(합의 없을 시) |
|---|------|---------------------|
| Q1 | `CounterServer/Program.cs`의 초기 모드를 **safe(`UseInterlocked=true`)** 로 둘 것인가, unsafe로 두어 경합 실패를 먼저 보여줄 것인가? | **safe 기본.** 정상 동작을 기준선으로 먼저 보이고, 주석으로 "한 줄 바꿔 실패를 재현하라"고 안내하는 편이 학습 순서상 자연스럽다. |
| Q2 | `CounterClient/Program.cs`를 `EchoClient`처럼 **인터랙티브**(콘솔 명령으로 add/sub/get)로 만들 것인가, **일괄 부하 후 리포트**(fire-and-report)로 만들 것인가? | **일괄 부하 후 리포트.** 요구사항의 핵심이 "동시 다발 송신으로 경합 유발 + 최종값 검증"이라 인터랙티브는 경합을 만들지 못한다. (원한다면 종료 전 `Enter` 시 재실행 루프 정도만 추가 가능.) |
