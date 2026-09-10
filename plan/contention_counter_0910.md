# 경합 카운터 서버 (Contention Counter) — 설계 문서

- **날짜:** 2026-09-10
- **기준 커밋:** `3aa7a80205ae84f96f6fe780a3cbde255178faf0`
- **개발 방식:** cross-verify 교차 검증 하네스 (Claude 계획 ↔ Codex 계획 → 조정 → 통합 계획 `13_final_plan.md` → 구현)
- **관련 산출물:** `_workspace/cross/20260910_163212_counter-server/`

---

## 1. 배경 및 목적

여러 클라이언트가 **하나의 공유 변수**를 동시에 갱신할 때 무슨 일이 일어나는지를 실제 소켓 위에서 보여 주는 **학습용 예제**가 필요했다. 기존 `EchoServer`/`EchoClient`는 요청-응답 1:1 구조라 세션 간 공유 상태가 없어 경합을 다루지 못한다.

이 예제가 가르치려는 것은 다음 한 문장이다.

> **경합은 없앨 수 없다. 경합 하에서도 정확하도록 만드는 것이 서버 프로그래밍이다.**

8개 연결이 초당 십수만 건으로 같은 메모리 워드를 두들겨도, 갱신이 `Interlocked` 원자 연산이면 최종값은 **항상 정확히** `연결 수 × (더하기 − 빼기)`가 된다. 이 결정성이 눈으로 확인되는 것이 학습 포인트다.

부수적으로 이 작업은 **cross-verify 하네스의 첫 실전 검증 대상**이기도 했다.

### 해결한 문제

| 문제 | 이 예제의 답 |
|---|---|
| 다중 세션이 같은 상태를 갱신하면 갱신이 유실되는가? | `Interlocked.Increment/Decrement`(단일 `lock xadd`)로 유실 0 |
| "최종값"을 언제 읽어야 의미가 있는가? | **연결별 완료 확인 → 전 연결 확인 후 최종 조회**(배리어). 조회 응답은 그 시점의 값일 뿐이다 |
| 최종값이 0일 때 "상쇄"와 "미처리"를 어떻게 구분하는가? | `AppliedOps`(적용된 증감 op 총수)를 함께 계수·검증 |

---

## 2. 설계 결정

### 2.1 채택안과 대안 비교

| 항목 | 채택 | 대안 | 채택 이유 |
|---|---|---|---|
| 증감 명령 패킷 | 기존 `IncrementPacket`(Id=3)·`DecrementPacket`(Id=4) **재사용** | 수량 필드를 가진 새 패킷 신설 | 두 패킷이 이미 "서버 변수를 1 증감"이라는 정확히 같은 의미로 존재했고 사용처가 없었다. 수량 필드는 경합 정확성 시연에 기여하지 않고 와이어 포맷만 늘린다 |
| 조회 방식 | `CounterQueryPacket`(Id=18, 0B) → `CounterValuePacket`(Id=19, 16B) 요청-응답 | 서버가 주기적으로 브로드캐스트 | 브로드캐스트는 "언제의 값인가"가 불분명해 **완료 확인(배리어)** 으로 쓸 수 없다. 요청-응답은 세션별 순차 처리 보장 덕에 그 자체가 배리어가 된다 |
| 응답 본문 | **16B** = `long Value` + `long AppliedOps` | 8B = `long Value`만 | `Value`만 보면 더하기 N + 빼기 N과 "아무것도 처리 안 함"이 똑같이 0이다. `AppliedOps`가 상쇄 은폐를 깨고, FAIL 시 원인을 *패킷 미도달* vs *갱신 유실*로 분해한다 |
| 공유 상태 | 서버 **인스턴스**당 `CounterState` 1개 | `static` 필드 | `static`이면 같은 프로세스의 테스트들이 카운터를 공유해 병렬 실행 시 서로 오염된다 |
| 동시성 제어 | `Interlocked` (lock-free) | `lock`/`Monitor` | 임계 구간이 "메모리 워드 1개 증감"뿐이다. 모니터 진입·이탈 비용과 경합 시 커널 대기 전환 비용이 연산 자체보다 훨씬 크다. `Interlocked`는 `lock xadd` 하나로 캐시라인을 배타 소유(RFO)해 컨텍스트 스위치 없이 완결한다 |
| 조회 대기 매칭 | 연결당 **미완료 조회 1건**으로 제한 | 요청 ID 필드 추가 | 1건 제한이면 응답 매칭이 연결 단위로 자명해져 요청 ID·본문이 불필요하다 |
| 요청 검증 위치 | 핸들러가 **직접** id·본문 길이 검증 | `Deserialize<T>`에 위임 | `BinaryPacketSerializer.Deserialize<T>`는 헤더를 **건너뛰기만** 하고 타입 ID 일치나 본문 전량 소비를 검사하지 않는다. 위임하면 "Id=3 자리에 다른 길이의 본문"이 조용히 통과한다 |
| 예제 타입 가시성 | `CounterState`·`CounterHandler`·`CounterScenario` **public** | `internal` + `InternalsVisibleTo` | 두 exe에 `InternalsVisibleTo`를 걸면 각 exe의 암묵 `internal partial class Program` 두 개가 동시에 테스트 어셈블리에 노출되어 `Program` 비수식 참조 시 **CS0104(모호한 참조)** 가 된다. 예제 exe는 NuGet 배포 대상(`pack.ps1`)이 아니므로 public화해도 공개 표면이 넓어지지 않고, **ServerLib의 Transport `internal` 캡슐화는 그대로 유지**된다 |
| 테스트 위치 | 신규 `CounterExample.Tests` | 기존 `EchoExample.Tests`에 추가 | 에코 테스트는 `ServerLib` 하나만 참조하는 구성이다. 여기에 예제 exe 2개 참조를 얹으면 에코 테스트의 책임과 의존성이 오염된다 |
| 실패 정책 | 프로토콜 위반 → 예외 → **해당 세션만** 종료 | 조용한 드롭 | 조용한 드롭은 프로토콜 불일치를 무증상으로 만들어 학습 예제의 진단성을 떨어뜨린다. `SocketPipelineSession` 디스패치 루프가 패킷 단위로 예외를 격리하므로 다른 세션·카운터는 무영향이다 |

### 2.2 완료 확인(배리어)이 정확성의 핵심인 이유

서버 세션 수신 루프는 **같은 세션**의 패킷을 순차 `await`로 처리한다(`SocketPipelineSession`의 디스패치 루프). 따라서

- 연결 A가 `[증감 ×1,750][조회]` 순으로 보냈다면, **조회 응답이 도착했다는 것 = A의 1,750회가 모두 적용됐다**는 뜻이다.
- 그러나 그것은 **A에 대해서만** 참이다. B~H는 아직 처리 중일 수 있다.

그래서 검증은 두 단계다.

```
[1단계] 각 연결: 마지막 증감 → 조회 → 응답 수신   (연결별 완료 확인)
[2단계] 8개 연결 모두 1단계 통과 후 → 최종 조회 1회 (전역 정지 상태)
```

**해서는 안 되는 것**
- 송신 완료(`await SendAsync`)만 기다리고 바로 조회 — 송신은 커널 버퍼 기록일 뿐 서버 처리 완료가 아니다.
- 중간 조회 응답 중 "마지막에 도착한 값"을 최종값으로 사용 — 다른 연결의 처리가 남아 있을 수 있다.

### 2.3 `Value`/`AppliedOps` 쌍의 비원자성

두 값은 **각각** 원자적으로 읽히지만 **둘을 한 번에** 읽는 연산은 원자적이지 않다. 갱신 진행 중에는 서로 다른 시점의 스냅샷일 수 있다.

→ **두 값을 함께 단언하는 것은 배리어 이후 정지 상태에서만 유효하다.** 이 제약을 `CounterState`·`CounterValuePacket`·`CounterSnapshot`의 `<remarks>`에 모두 명시했다.

두 값을 하나의 `long`에 비트 패킹하면 단일 CAS로 원자적 쌍 읽기가 가능하지만, 각 필드가 32비트로 좁아져 학습 예제의 값 범위를 잃는다. 정지 상태 단언만 필요하므로 필드 분리 + 문서화를 택했다.

---

## 3. 컴포넌트 구조

```
ClaudeCodeStudy/
├── ServerLib/Core/Serialization/Packets/
│   ├── CounterQueryPacket.cs      (신규) Id=18, 본문 0B — struct
│   └── CounterValuePacket.cs      (신규) Id=19, 본문 16B — struct
├── CounterServer/                 (신규 프로젝트, 콘솔 exe)
│   ├── CounterState.cs            공유 카운터 — Interlocked long 2개
│   ├── CounterHandler.cs          프레임 검증 → 라우팅 → 갱신/조회 응답
│   └── Program.cs                 127.0.0.1:9300 리스너 와이어링 (예제 본체)
├── CounterClient/                 (신규 프로젝트, 콘솔 exe)
│   ├── CounterScenario.cs         CounterSnapshot / Options / Result / RunAsync 실행기
│   └── Program.cs                 설정 상수 → RunAsync → PASS/FAIL·종료 코드
└── CounterExample.Tests/          (신규 프로젝트, xUnit)
    ├── CounterPacketTests.cs      와이어 포맷 (Fact 6 + Theory 6케이스 = 12)
    ├── CounterStateTests.cs       상태 동시 갱신 (6)
    └── CounterEndToEndTests.cs    실소켓 E2E·실패 경로 (8)   ── 합계 26
```

### 의존 관계

```
CounterExample.Tests ──┬──> CounterServer ──> ServerLib
                       ├──> CounterClient ──> ServerLib
                       └──> ServerLib
```

- 예제 → ServerLib 단방향. ServerLib은 예제를 참조하지 않는다.
- 테스트는 **실제** `CounterHandler`(서버 알고리즘)와 **실제** `CounterScenario.RunAsync`(클라이언트 알고리즘)를 호출한다. 알고리즘을 복제하면 예제와 테스트가 갈라져 "테스트는 통과하는데 예제는 틀린" 상태가 생긴다.
- `InternalsVisibleTo`는 사용하지 않는다(§2.1 참조).

### 와이어 포맷

```
헤더 4B: [PacketId(2, LE)] [BodyLength(2, LE)]

Id=3  IncrementPacket    본문 0B    카운터 +1
Id=4  DecrementPacket    본문 0B    카운터 −1
Id=18 CounterQueryPacket 본문 0B    현재 값 조회
Id=19 CounterValuePacket 본문 16B   [long Value(8)] [long AppliedOps(8)]
```

기존 사용 ID(1~17)·하트비트 예약(0xFFFE/0xFFFF)과 충돌하지 않는다. 테스트용 미지 ID로는 250을 쓴다 — 서버는 정상 PING(0xFFFE)만 하트비트로 가로채므로 그 외 ID는 앱 경로(`CounterHandler`)에 도달한다.

---

## 4. 핵심 API

### 4.1 서버 (`CounterServer/Program.cs`)

```csharp
var state   = new CounterState();          // 이 예제의 경합 지점 — 모든 세션이 이 인스턴스 하나를 갱신
var handler = new CounterHandler(state);

IServerListener listener = ServerNet.CreateListener();
listener.OnReceived  = handler.HandleAsync; // 예제와 테스트가 공유하는 바로 그 핸들러
listener.OnClientError = (session, ex) => { /* 로그 후 해당 세션만 종료 */ };
listener.Start(9300, IPAddress.Loopback);   // 루프백 전용 — 인증 없는 카운터를 외부에 노출하지 않는다
```

### 4.2 핸들러 라우팅

```csharp
if (!PacketPool.TryParseHeader(frame, out ushort id, out int bodyLength)) throw ...;
if (frame.Length != PacketPool.HeaderSize + bodyLength) throw ...;   // 선언 vs 실제 교차 검증

switch (id)
{
    case IncrementPacket.Id:    RequireBodySize(id, bodyLength, 0); State.Increment(); return ValueTask.CompletedTask;
    case DecrementPacket.Id:    RequireBodySize(id, bodyLength, 0); State.Decrement(); return ValueTask.CompletedTask;
    case CounterQueryPacket.Id: RequireBodySize(id, bodyLength, 0); return SendCurrentValueAsync(session);
    default: throw new InvalidDataException($"알 수 없는 패킷 ID: {id}");
}
```

**경로별 완료·할당 특성이 다르다**(XML 문서에 분리 서술).

| 경로 | 완료 | 할당 |
|---|---|---|
| 증감 (Id 3·4) | 동기 갱신 후 이미 완료된 `ValueTask` | **무할당** (상태머신·로그·작업 Task 없음) |
| 조회 응답 (Id 18) | 송신 동기 완료 시 즉시, 미완료 시 비동기 | 동기 완료 시 무할당, 미완료 시 **상태머신 1개 조건부** (`PacketSendExtensions.cs:78-88`) |

### 4.3 클라이언트 (`CounterClient/Program.cs`)

```csharp
var options = new CounterScenarioOptions
{
    Host = "127.0.0.1", Port = 9300,
    ConnectionCount = 8,
    IncrementsPerConnection = 1_000,
    DecrementsPerConnection = 750,
    Timeout = TimeSpan.FromSeconds(30),
};

CounterScenarioResult result = await CounterScenario.RunAsync(options);
// result.Passed  ← Value == 2,000  AND  AppliedOps == 14,000
return result.Passed ? 0 : 1;   // 통신 오류·타임아웃은 예외 → 2
```

### 4.4 hot loop의 무할당 송신

증감 프레임은 본문이 없어 결과가 항상 동일하다. 실행 전 **1회 직렬화**해 정적 `ReadOnlyMemory<byte>`로 보관하고 재사용한다.

```csharp
private static readonly ReadOnlyMemory<byte> IncrementFrame = CreateFrame(new IncrementPacket());
// ...
await connection.SendFrameAsync(isDecrement ? DecrementFrame : IncrementFrame, ct);
```

`PacketSendExtensions.SendAsync<T>`를 매번 호출하면 송신마다 ArrayPool Rent/Return + Serialize가 반복된다. 14,000회 루프에서는 프레임 재사용이 명백히 유리하다.

### 4.5 증감 순서 섞기 (Bresenham + 위상 회전)

더하기를 앞에, 빼기를 뒤에 몰면 구간별로 단조 증감만 하여 인터리빙이 약해진다. 빼기를 전 구간에 균등 분산하고, 연결마다 읽기 시작 위치를 회전시킨다. **회전은 항목 수를 바꾸지 않으므로 연결별 더하기·빼기 횟수는 정확히 보존**된다(기대값의 전제).

---

## 5. 변경 파일 목록

### 신규

| 파일 | 내용 |
|---|---|
| `ServerLib/Core/Serialization/Packets/CounterQueryPacket.cs` | Id=18, 본문 0B struct. 완료 확인 용도를 `<remarks>`에 명시 |
| `ServerLib/Core/Serialization/Packets/CounterValuePacket.cs` | Id=19, 본문 16B struct. 두 값 쌍의 비원자성 명시 |
| `CounterServer/CounterServer.csproj` | .NET 10 콘솔, ServerLib 참조 |
| `CounterServer/CounterState.cs` | Interlocked `long` 2개(`_value`·`_appliedOps`). 락 미사용 근거 주석 |
| `CounterServer/CounterHandler.cs` | 헤더 검증·라우팅·상태 갱신·조회 응답. 증감/조회 경로별 완료·할당 특성 분리 문서화 |
| `CounterServer/Program.cs` | 9300 루프백 리스너 와이어링. 종료 시 참고용 `Value` 출력(검증 판정 아님) |
| `CounterClient/CounterClient.csproj` | .NET 10 콘솔, ServerLib 참조 |
| `CounterClient/CounterScenario.cs` | `CounterSnapshot`·`CounterScenarioOptions`·`CounterScenarioResult`·`RunAsync` + 내부 `CounterConnection` |
| `CounterClient/Program.cs` | 학습용 설정 상수, 실행기 호출, PASS/FAIL·종료 코드(0/1/2) |
| `CounterExample.Tests/CounterExample.Tests.csproj` | xUnit. ServerLib·CounterServer·CounterClient 3개 ProjectReference |
| `CounterExample.Tests/CounterPacketTests.cs` | ID·본문 길이(0/0/0/16)·프레임 길이(20)·경계값 왕복 |
| `CounterExample.Tests/CounterStateTests.cs` | 실제 `CounterState` 동시 갱신 — 더하기만/빼기만/불균형/균형 |
| `CounterExample.Tests/CounterEndToEndTests.cs` | 실소켓 E2E + 실패 경로. 실제 `CounterHandler`·`CounterScenario` 사용 |
| `plan/contention_counter_0910.md` | 본 문서 |

### 수정

| 파일 | 내용 |
|---|---|
| `ClaudeCodeStudy.sln` | 신규 프로젝트 3개 등록 (구성 매핑 8프로젝트 × 12행 = 96행 확인) |
| `CLAUDE.md` | 예제 목록에 CounterServer·CounterClient 2행, plan 문서 표에 1행 |
| `AGENTS.md` | 위와 동일(양쪽 문서 대칭 유지) |

---

## 6. 빌드 검증

```powershell
dotnet build ClaudeCodeStudy.sln -c Release
dotnet test  CounterExample.Tests/CounterExample.Tests.csproj -c Release --no-build
dotnet test  ClaudeCodeStudy.sln -c Release --no-build
```

수동 콘솔 데모 (서버는 `Console.ReadKey`를 쓰므로 **자기 콘솔**이 필요하다 — stdin이 리다이렉트되면 즉시 예외로 죽는다):

```powershell
$srv = Start-Process CounterServer\bin\Release\net10.0\CounterServer.exe -PassThru
CounterClient\bin\Release\net10.0\CounterClient.exe    # PASS + 종료 코드 0
Stop-Process -Id $srv.Id -Force
```

### 실측 결과 (2026-09-10)

| 항목 | 결과 |
|---|---|
| `dotnet build ClaudeCodeStudy.sln -c Release` | 오류 0, 경고 0 |
| `dotnet test CounterExample.Tests` | **26 통과 / 0 실패** |
| `dotnet test ClaudeCodeStudy.sln` (3회 반복) | **40 통과 / 0 실패** (기존 Echo 14 + 신규 26), 3회 모두 동일 |
| 수동 콘솔 데모 | `PASS`, Value 2,000 / AppliedOps 14,000, **종료 코드 0** |
| 8연결 × 1,750회(14,000패킷) 실행 시간 | 약 **0.12초** (루프백) — 타임아웃 상수 30초 대비 250배 여유 |

---

## 7. 검증의 한계 (명시적 기록)

- **배리어의 필요성은 어떤 테스트로도 결정적으로 증명되지 않는다.** E2E는 "배리어 경로가 기대값을 산출한다"까지만 보장한다. 배리어를 제거했을 때의 결함은 비결정적 오값으로만 드러나고, 그것을 단언하려는 순간 flaky 테스트가 된다. 배리어의 필요성은 설계 근거(세션별 순차 처리)로만 성립한다.
- 서버 종료 시 출력하는 `Value`는 정지 보장이 없어 **참고용**이다. 검증 판정은 클라이언트의 배리어 이후 최종 조회로만 내린다.
- 실행 전제는 **새 서버 + 단일 클라이언트 실행**이다. 카운터는 서버 프로세스 수명 동안 누적되므로 재검증 시 서버를 재시작해야 한다(클라이언트가 시작 시점 스냅샷이 0이 아니면 경고를 출력한다).
- 경합의 **실제 CPU 실행 겹침 정도**는 스케줄러에 좌우된다. 이 예제는 정확성을 보장할 뿐 특정 수준의 물리적 동시성을 보장하지 않는다.

---

## 8. 향후 확장 포인트

| 후보 | 내용 | 비고 |
|---|---|---|
| **unsafe(비원자) 토글** | `CounterState`에 `_value++`(읽기-수정-쓰기 3단계) 경로를 옵션으로 추가해, 같은 부하에서 **최종값이 기대값보다 작아지는 것**을 눈으로 보여 준다 | cross-verify 조정 D2에서 이번 범위 밖으로 **기각**됨. 학습 효과는 크지만 비결정적 실패 시연이라 자동 테스트에 넣을 수 없다 |
| 수량 필드 증감 | `Add(delta)` 형태로 확장 | 현재는 ±1 고정 |
| 다중 카운터 | 카운터 ID로 샤딩해 false sharing / 캐시라인 패딩 실험 | 64B 패딩 유무 비교가 좋은 후속 학습 소재 |
| 경합 계측 | 초당 처리량·지연 분포 측정 | 이번 범위는 정확성 시연이라 비범위였다 |
| 대안 동시성 기법 비교 | `Interlocked` vs `lock` vs `Channel` 단일 소비자 직렬화의 처리량 비교 | 현업에서 실제로 갈리는 선택지 |

---

## 부록: 베이스라인 사실 교정

교차 검증 과정에서 Codex 계획서(`10_codex_plan.md` 3행)가 *"미추적 `.claude/settings.local.json`이 있어 워킹트리가 완전히 깨끗하지는 않다"* 고 기술했으나, 해당 파일은 **전역 ignore 대상**이며 `git status --porcelain`은 빈 출력이었다. 기준 커밋 시점의 워킹트리는 깨끗했다. (조정 D14 / 최종 점검 F-4)
