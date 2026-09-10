# 20_impl_notes — 경합 카운터 서버 구현 노트

- **구현자:** cross-implementer (Claude)
- **입력:** `00_context.md`, `13_final_plan.md`(베이스 `10_codex_plan.md`), `14_claude_final_check.md` §3 확정 해석 F-1·F-2, `14_codex_final_check.md`
- **결과:** 계획 전량 구현 완료. **범위 이탈 0건.** 빌드 오류 0·경고 0, 테스트 40/40 통과(기존 14 유지 + 신규 26), 수동 콘솔 데모 PASS·종료 코드 0.
- **커밋:** 하지 않음. `.git/auto_commit_msg.txt` 생성하지 않음(지시 준수).

---

## 1. 계획 대비 구현 대조

### 1.1 통합 계획 수정 사항 10건

| # | 수정 요구 | 구현 위치 | 상태 |
|---|---|---|---|
| 1 | 응답 16B(`Value`+`AppliedOps`), `AppliedOps`는 항상 `Interlocked.Increment`, 클라가 2,000·14,000 둘 다 `checked` 검증, 쌍의 비원자성 주석 필수 | `CounterValuePacket.cs`(BodySize=16), `CounterState.Increment/Decrement`(둘 다 `_appliedOps` 증가), `CounterScenario.RunAsync`의 `checked` 기대값 2개, 비원자성 `<remarks>`는 `CounterValuePacket`·`CounterState`·`CounterSnapshot`·`CounterHandler.SendCurrentValueAsync` **4곳** | 반영 |
| 2 | 베이스 §6 "마지막 명령을 테스트용 비동기 신호로 지연" 항목 **구현하지 않음** | 해당 테스트 없음. E2E는 실제 `CounterScenario.RunAsync` 호출 | 반영 |
| 3 | `CLAUDE.md`·`AGENTS.md` **양쪽**에 예제 2행 + plan 표 1행 | CLAUDE.md L17-18/L91, AGENTS.md L17-18/L92 | 반영 |
| 4 | 증감 경로 "동기 갱신 후 완료된 ValueTask·무할당" / 조회 응답 "송신 미완료 시 비동기·조건부 할당(`PacketSendExtensions.cs:78-88`)" 분리 서술 | `CounterHandler` 클래스 `<remarks>`에 두 개의 `<item>`으로 분리, `SendCurrentValueAsync`에 재기술 | 반영 |
| 5 | 조회 응답 송신 실패를 "상대 종료"로 단정 금지, 로그 후 세션 종료, 자동 재송신 금지 | `SendCurrentValueAsync` `<remarks>`에 `SocketError.TimedOut` 변환 근거 명시. 예외를 그대로 전파해 수신 루프가 `OnClientError`(Program이 로그) → 세션 종료. 재송신 코드 없음 | 반영 |
| 6 | 서버 종료 시 참고용 `Value`만 출력(판정 아님) | `CounterServer/Program.cs` 마지막 3줄 — 주석으로 "정지 보장 없음, 판정 아님" 명시 | 반영 |
| 7 | 미지 패킷 Id=250 유지, 근거는 "정상 PING(0xFFFE)만 가로챔" | `CounterEndToEndTests.UnknownPacketId = 250` + 해당 문구 그대로 주석 | 반영 |
| 8 | 연결 8·+1,000/−750·2,000·14,000, 타임아웃은 실측으로 결정하되 상수로 명시 | 전부 명명 상수. E2E 타임아웃 30초는 실측 0.12초 기반(§3.3) | 반영 |
| 9 | sln 구성 매핑 12행/프로젝트 확인 | 8프로젝트 × 12 = 96행 확인(`20_test_results.txt` [6]) | 반영 |
| 10 | 예제 내부 타입 접근 방식 | **F-1 확정에 따라 public + `InternalsVisibleTo` 미사용**(아래) | 반영 |

### 1.2 최종 점검 확정 해석 (14_claude_final_check §3)

**F-1 — public 확정.** `CounterState`·`CounterHandler`·`CounterScenario`(및 `CounterScenarioOptions`/`Result`/`CounterSnapshot`)를 전부 `public`으로 선언했다. `InternalsVisibleTo`는 **어느 프로젝트에도 넣지 않았다.** `CounterExample.Tests.csproj`는 `ServerLib`·`CounterServer`·`CounterClient` 3개를 평범한 `ProjectReference`로 참조한다. 무효화된 베이스 문장 3곳(베이스 §3의 "internal 구현으로 둔다", §2 표의 "테스트 어셈블리에만 내부 접근 허용" 2행)은 구현하지 않았다. ServerLib의 Transport `internal` 캡슐화는 그대로다.

**F-2 — 전부 16B.** 베이스에 남아 있던 8B 기술 4곳을 전부 16으로 읽었다. 특히 클라이언트 응답 길이 검증 상수는 **본문 16**이며, 프레임 검증은 `frame.Length == PacketPool.HeaderSize + 16`(=20)로 별도 확인한다 — 본문/프레임을 혼동한 off-by-4를 막기 위해 `CounterPacketTests`에서 두 값을 분리 단언했다. `AppliedOps`는 검증을 통과해 실제 적용된 증감 op(Id 3·4)만 계수하며 조회·드롭·미지 패킷은 제외한다(`CounterHandler`가 `State.Increment/Decrement` 호출 직전에 모든 검증을 마친다).

### 1.3 비차단 지적 F-3·F-5·F-6

- **F-5(문서 누락 방지):** `CLAUDE.md`·`AGENTS.md`를 변경 파일 목록의 체크 항목으로 취급해 양쪽 모두 갱신했고, 설계 문서 §5 "수정" 표에도 2행으로 등재했다.
- **F-6(상태 불변의 범위):** 잘못된 본문 길이·미지 ID 테스트 모두 `Value`와 `AppliedOps` **둘 다** 0임을 단언한다(각 2개 단언).
- **F-3(수치 상수화):** `CounterStateTests`(TaskCount 8 / IncrementsPerTask 10,000 / DecrementsPerTask 7,500), "단일 연결 증감"(5/2), "총 연산 0"(4연결 × 500/500), 미지 ID(250), 타임아웃 4종을 전부 명명 상수로 두고 기대값을 `checked`로 계산했다.

---

## 2. 구현 상세 — 계획에 없던 판단이 필요했던 지점

전부 계획 범위 **안**의 구현 세부이며 설계 변경이 아니다. 리뷰어가 근거를 확인할 수 있도록 남긴다.

### 2.1 증감 순서 섞기 방식

베이스 §1은 "연결마다 순서를 달리해 증감 유입이 겹치도록 한다"만 요구하고 방식을 지정하지 않았다. Bresenham(빼기를 전 구간 균등 분산) + **연결별 읽기 시작 위치 회전**을 택했다.

- 앞쪽에 더하기를 몰면 구간별로 단조 증가만 하여 인터리빙이 약해진다.
- **회전은 항목 수를 바꾸지 않으므로** 연결별 더하기·빼기 횟수가 정확히 보존된다 — 기대값 2,000/14,000의 전제가 깨지지 않는다.
- 스케줄은 실행당 `bool[]` 1개이고 생성 후 변경하지 않으므로 전 연결 Task가 락 없이 공유한다.

`CounterStateTests`도 같은 방식을 쓴다(첫 시도에서 "위상만 뒤집는" 변형을 썼다가 빼기 횟수가 흔들려 즉시 폐기했다).

### 2.2 실패 표면화 방식: 예외 vs 결과 플래그

- **값 불일치** → `CounterScenarioResult.Passed = false` (정상 반환)
- **통신 오류·연결 끊김·프로토콜 위반 응답** → 예외 전파
- **전체 기한 만료** → `TimeoutException` (호출자 취소와 구분해 변환)

이렇게 나눈 이유: 결과 객체를 받았다는 것 자체가 "통신은 끝까지 성공했다"는 뜻이 되어, 호출자가 부분 실패를 PASS로 오인할 경로가 사라진다. 베이스 §5의 "일부 작업 실패 후 PASS를 출력하지 않는다"를 타입 수준에서 강제한 것이다. `Program.cs` 종료 코드는 0=PASS / 1=값 불일치 / 2=통신 오류·타임아웃.

### 2.3 조회 대기자의 소유권 이전

베이스 §4의 "응답·연결 종료 경합은 `Interlocked`와 `TrySet…`으로 처리"를 다음으로 구현했다.

- 대기자 참조를 `Interlocked.Exchange(ref _pendingQuery, null)`로 **꺼내 가는** 쪽이 완료 권한을 갖는다. `TaskCompletionSource` 자체의 `TrySetResult`가 스레드 안전하더라도, "꺼내 가는" 행위까지 원자적이어야 응답과 연결 해제가 동시에 도착했을 때 이중 처리되지 않는다.
- 대기자는 **송신 전에** 게시한다. 루프백은 응답이 먼저 도착할 만큼 빠르다.
- 송신 실패·취소로 빠져나갈 때 `Interlocked.CompareExchange(ref _pendingQuery, null, waiter)`로 **값 비교 회수**한다. 그 사이 IO 스레드가 이미 가져갔으면 건드리지 않는다.
- `OnDisconnected`에서 대기자를 즉시 실패시킨다. 이 배선이 없으면 "조회 중 서버 종료" 경로가 전체 기한이 만료될 때까지 매달려, 원인이 '연결 끊김'인지 '무응답'인지 구분되지 않는다.

### 2.4 클라이언트 `OnReceived`에서 예외를 던지지 않는다

프로토콜에 맞지 않는 응답은 `waiter.TrySetException(...)`으로 **전달**하고 콜백은 정상 반환한다. 콜백에서 던지면 클라이언트 수신 루프가 죽어 실패 원인이 뒤섞인다.

### 2.5 hot loop 무할당 송신

증감 프레임(각 4B, 본문 없음)은 결과가 항상 동일하므로 정적으로 1회 직렬화해 `ReadOnlyMemory<byte>`로 재사용한다. `PacketSendExtensions.SendAsync<T>`를 14,000회 호출하면 매번 ArrayPool Rent/Return + Serialize가 반복된다. 베이스 §4의 "증감 프레임 두 개는 실행 시작 시 한 번 직렬화한 작은 배열로 보관"을 그대로 구현한 것이다(조회 프레임도 같은 이유로 정적화).

### 2.6 테스트가 원시 프레임을 직접 조립하는 범위

주 시나리오(테스트 3·4·7·8)는 실제 `CounterScenario.RunAsync`를 호출하고, 서버 측은 모든 테스트가 실제 `CounterHandler.HandleAsync`를 `OnReceived`에 연결한다. **알고리즘 복제는 없다.**

프로토콜 실패 경로(잘못된 본문 길이, 미지 ID)와 하위 계층 왕복(테스트 1·2)만 `RawCounterClient` 헬퍼로 원시 프레임을 다룬다 — 예제 API로는 **만들 수 없는 프레임**이기 때문이며, `EchoExample.Tests`가 쓰는 것과 동일한 패턴이다.

---

## 3. 검증 중 발견·해소한 문제

### 3.1 실패 경로 테스트가 무의미하게 통과할 뻔한 지점 (수정함)

"잘못된 본문 길이 → 상태 불변"을 프레임 송신 직후에 단언하면, 서버가 아직 처리하기 전이라 **아무것도 검증하지 않고 통과**한다. 서버의 `OnClientError` 신호(`TaskCompletionSource<Exception>`)를 배리어로 삼아, 세션이 실제로 오류 종료한 뒤에만 `Value`·`AppliedOps`를 단언하도록 했다. 예외 타입(`InvalidDataException`)과 메시지 접두사(`CounterHandler.InvalidBodyMessagePrefix` / `UnknownPacketMessagePrefix`)까지 확인해 "다른 이유로 끊긴 것"과 구분한다.

### 3.2 "서버 중도 종료" 테스트의 경주 조건 (실패 → 원인 제거 → 재검증)

최초 설계는 "서버가 N번째 패킷에서 `listener.Stop()`"이었는데, 전체 솔루션 병렬 실행에서 **실제로 실패했다**(원문은 `20_test_results.txt` [4]). `Stop()`을 `Task.Run`으로 스케줄하는 동안 시나리오가 먼저 완주할 수 있는 경주였다 — 테스트 자체의 결함이며 제품 코드 결함이 아니다.

시간이 아니라 **프로토콜 진행 상태**에 결부시켜 해소했다: 서버가 **두 번째 조회부터는 응답하지 않고** 종료한다. 첫 조회는 `RunAsync`의 기준값 조회이고 두 번째부터가 배리어 조회이므로, 최소 한 연결은 반드시 확인 응답을 받지 못한다 → 시나리오는 구조적으로 완료 불가능 → 결정적 실패. 이후 3회 반복 전부 통과.

### 3.3 타임아웃 상수의 실측 근거

8연결 × 1,750회(14,000패킷) 왕복이 루프백에서 **약 0.12초**였다(단독 실행 119ms, 수동 데모 0.12초). E2E·클라이언트 기본 타임아웃 30초는 약 250배 여유이므로, CI 스케줄링 지연이 수십 배가 되어도 오탐이 나지 않는다. `RoundTripTimeoutMs`는 기존 에코 테스트 관례(5초)를 따랐다.

### 3.4 자체 리뷰로 추가 제거한 테스트 결함 3건 (제품 코드 변경 아님)

전부 `CounterExample.Tests/CounterEndToEndTests.cs` 한 파일 안의 테스트 코드 수정이다. 적용 후 전체 스위트를 다시 3회 실행해 40/40 통과를 확인했다(`20_test_results.txt` [4-b], 상세는 [8]).

1. **`Scenario_ServerStopsMidRun`의 `Stop()` 중복 호출 (잠재 결함).** 조건이 "두 번째 조회부터"였기 때문에 드롭되는 배리어 조회마다 `Task.Run(listener.Stop)`이 스케줄되어 최대 4회 + `finally` 1회가 겹칠 수 있었다. `Stop()`은 활성 세션을 순회하며 `DisposeAsync().GetResult()`를 호출하므로 두 스레드가 동시에 들어가면 이중 Dispose로 `ObjectDisposedException`이 날 수 있고, 그 예외가 실제 단언 결과를 가린다. → 카운터 값이 **정확히 2**일 때만 1회 스케줄하고 그 Task 핸들을 `finally`에서 `await`하도록 고쳤다. 이 테스트의 결정성은 "응답을 드롭한다"에서 나오므로 `Stop()` 1회로 충분하다.

2. **`Scenario_UnresponsiveServer`가 "정리"를 검증하지 않았다.** 계획의 실패 항목은 *"무응답 타임아웃(실제 작업 취소·정리 포함)"* 인데, 기존 단언은 `TimeoutException`이 마진 안에 던져졌다는 **반환**만 확인했다. `RunAsync`의 `finally` 정리 루프가 일부 연결을 빠뜨려도 통과한다. → 테스트 리스너에 `OnClientDisconnected`를 등록해 해제 수를 `Interlocked`로 집계하고, 예외를 잡은 뒤 "연결 수(2)만큼 해제됨"을 신호로 대기·단언하도록 강화했다. 계획 문구가 요구한 검증이 실제로 이뤄지지 않던 유일한 지점이었다.

3. **`RawCounterClient.OnReceived`의 무검증 `Deserialize`.** id·길이 검증 없이 `Deserialize<CounterValuePacket>`을 호출하고 있었다. 짧은 프레임이 오면 `EndOfStreamException`이 콜백 밖으로 새어 수신 루프가 죽고, 명확한 단언 실패가 "타임아웃까지 hang"으로 퇴화한다. → 대기자를 먼저 회수한 뒤 id(19)·본문(16B)·프레임(20B)을 검증하고, 위반 시 던지지 않고 `TrySetException`으로 전달하도록 고쳤다(§2.4의 원칙을 헬퍼에도 적용).

### 3.5 수동 데모 실행 방식

서버는 EchoServer 패턴대로 `Console.ReadKey(intercept: true)`로 종료를 대기한다. stdin이 리다이렉트된 도구 호출에서는 `InvalidOperationException`으로 즉시 죽는다. **예제의 종료 설계를 바꾸지 않고** `Start-Process`로 서버에 자기 콘솔을 주어 실행했다(§`20_test_results.txt` [5]).

---

## 4. 보고 의무 항목 (최종 점검이 승인에 포함시킨 것)

- **F-3 — 검증의 한계:** **배리어의 필요성은 어떤 테스트로도 결정적으로 증명되지 않는다.** E2E는 "배리어 경로가 기대값을 산출한다"까지만 보장한다. 배리어 결함은 비결정적 오값으로만 드러나고, 그것을 단언하려는 순간 flaky 테스트가 된다. 배리어의 필요성은 설계 근거(서버 수신 루프의 세션별 순차 처리)로만 성립한다. 이 한계를 `CounterEndToEndTests` 클래스 `<remarks>`와 설계 문서 §7에 기록했다.
- **F-4 — 베이스라인 사실 교정:** 베이스 계획 3행의 *"미추적 `.claude/settings.local.json`이 있어 워킹트리가 완전히 깨끗하지는 않다"* 는 부정확하다. 해당 파일은 전역 ignore 대상이며 기준 커밋 시점 `git status --porcelain`은 빈 출력이었다. 설계 문서 부록에 교정 기록.
- **F-7 / D2 — 후속 확장 후보:** **unsafe(비원자) 카운터 토글**(`_value++` 3단계 경로를 옵션으로 두어 같은 부하에서 최종값이 기대값보다 작아지는 것을 시연)을 이번 범위에서 기각했으며, 설계 문서 §8 향후 확장 포인트 표에 첫 항목으로 기록했다. 학습 효과는 크지만 비결정적 실패 시연이라 자동 테스트에 넣을 수 없다는 단서를 함께 남겼다.

---

## 5. 프로젝트 규칙 준수

- **XML 문서 주석:** 신규 public 타입·멤버 전체에 `<summary>`/`<param>`/`<returns>`/`<exception>`/`<remarks>`. `<remarks>`에는 **Thread Safety·Memory Allocation(소유권·생명주기 포함)·Blocking**을 빠짐없이 명시했고, 수정 4에 따라 증감/조회 경로의 완료·할당 특성을 분리 서술했다.
- **네트워크·메모리 선언부 인라인 주석:** `Interlocked` 대상 필드(`_value`·`_appliedOps`·`_pendingQuery`·테스트의 `queryCount`), `ReadOnlyMemory<byte>` 프레임 상수, `ArrayPool` 경유 송신, `TaskCompletionSource`(+`RunContinuationsAsynchronously`), `CancellationTokenSource.CreateLinkedTokenSource`, `IClientConnection`/`IServerListener` 팩토리 호출, `bool[]` 불변 공유 배열, `ReadOnlySpan<byte>` zero-copy 뷰 전부에 **내부 동작 메커니즘을 근거로 한** 이유 주석을 달았다.
- **동시성:** 전통적 락 0개. 전부 `Interlocked` 기반이므로 `[LOCK-REQUIRED]` 정당화 주석 대상이 없다. `CounterState`에는 락을 쓰지 **않은** 근거(임계 구간이 워드 1개 증감이라 모니터 진입·커널 대기 전환 비용이 연산보다 크다 / `lock xadd` 단일 명령으로 완결)를 반대 방향으로 명시했다.
- **의존성 방향:** 예제 → ServerLib 단방향. ServerLib은 예제를 참조하지 않는다. Interface에는 아무 변경도 하지 않았다.
- **예제 갱신:** 신규 기능이므로 `CounterServer/Program.cs`·`CounterClient/Program.cs` 자체가 라이브러리 사용 예제다.
- **설계 문서:** `plan/contention_counter_0910.md` — CLAUDE.md가 요구하는 7개 필수 항목(배경·설계 결정·컴포넌트 구조·핵심 API·변경 파일·빌드 검증·향후 확장) 전부 포함 + 검증 한계(§7)·베이스라인 교정(부록) 추가.

---

## 6. [범위 이탈]

**없음.** 계획에 명시되지 않은 설계 변경·신규 공개 API·계획 밖 파일 추가는 하지 않았다.

참고로, 계획의 변경 파일 목록에 없지만 추가한 **파일은 없다**. `CounterSnapshot`·`CounterScenarioOptions`·`CounterScenarioResult`는 별도 파일을 만들지 않고 계획에 등재된 `CounterClient/CounterScenario.cs` 안에 함께 선언했다(실행기의 입출력 타입이므로 같은 파일이 자연스럽고, 계획 파일 목록을 벗어나지 않는다).

---

## 7. 산출물

**신규 코드**
- `E:\project\ClaudeCodeStudy\ServerLib\Core\Serialization\Packets\CounterQueryPacket.cs`
- `E:\project\ClaudeCodeStudy\ServerLib\Core\Serialization\Packets\CounterValuePacket.cs`
- `E:\project\ClaudeCodeStudy\CounterServer\CounterServer.csproj`
- `E:\project\ClaudeCodeStudy\CounterServer\CounterState.cs`
- `E:\project\ClaudeCodeStudy\CounterServer\CounterHandler.cs`
- `E:\project\ClaudeCodeStudy\CounterServer\Program.cs`
- `E:\project\ClaudeCodeStudy\CounterClient\CounterClient.csproj`
- `E:\project\ClaudeCodeStudy\CounterClient\CounterScenario.cs`
- `E:\project\ClaudeCodeStudy\CounterClient\Program.cs`
- `E:\project\ClaudeCodeStudy\CounterExample.Tests\CounterExample.Tests.csproj`
- `E:\project\ClaudeCodeStudy\CounterExample.Tests\CounterPacketTests.cs`
- `E:\project\ClaudeCodeStudy\CounterExample.Tests\CounterStateTests.cs`
- `E:\project\ClaudeCodeStudy\CounterExample.Tests\CounterEndToEndTests.cs`

**신규 문서**
- `E:\project\ClaudeCodeStudy\plan\contention_counter_0910.md`

**수정**
- `E:\project\ClaudeCodeStudy\ClaudeCodeStudy.sln` (프로젝트 3개 등록, 96행 구성 매핑)
- `E:\project\ClaudeCodeStudy\CLAUDE.md` (예제 2행 + plan 표 1행)
- `E:\project\ClaudeCodeStudy\AGENTS.md` (동일)

**하네스 산출물**
- `E:\project\ClaudeCodeStudy\_workspace\cross\20260910_163212_counter-server\20_impl_notes.md` (본 문서)
- `E:\project\ClaudeCodeStudy\_workspace\cross\20260910_163212_counter-server\20_test_results.txt`
