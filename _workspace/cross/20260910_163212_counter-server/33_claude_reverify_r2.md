# 33_claude_reverify_r2 — Claude 측 수정 재검토 (r2, 완전 교차 검증)

대상: `31_review_adjudication_r2.md`가 유효 판정한 R-X1~X4 + R-C6. 구현자 주장(`32_fix_notes_r2.md`)을 믿지 않고 리포 코드·diff를 직접 열어 검증. `dotnet test CounterExample.Tests` 독립 1회 실행(28/28) 포함. 코드가 이전 라운드와 달라졌으므로 이전 승인은 인용하지 않고 변경분 전체를 재확인함.

검증한 실제 파일:
- `CounterClient/CounterScenario.cs`
- `CounterServer/CounterHandler.cs`
- `CounterExample.Tests/CounterEndToEndTests.cs`
- `ServerLib/Core/Serialization/PacketSendExtensions.cs` (R-X4 근거 대조용, 미수정 확인)

---

## R-X1 (Med) — Validate 합계 0 허용 + 빈 실행 E2E — **해소**

**Validate가 실제로 합계 0을 허용하는가:** `CounterScenario.cs:70-71`이 `ThrowIfLessThanOrEqual(..,0,..)` → `ThrowIfNegative(Inc+Dec, ..)`로 교체됨. `Inc=Dec=0`이면 합계 0이 통과(음수만 거부). 삭제가 아니라 `ThrowIfNegative` 교체로 한 것도 근거 유효 — 두 값이 각각 위(L65·66)에서 음수 배제되므로 합계 음수의 유일 경로는 int 덧셈 오버플로이고, 이를 `RunAsync`의 `checked` 곱셈(`OverflowException`) 이전에 `ArgumentOutOfRangeException`으로 잡아 XML 예외 계약을 유지함. 정확.

**빈 실행이 배리어를 실제로 통과하는가(빈 경로 통과 아님):** `RunAsync` 코드 추적 — `Inc=Dec=0` → `opsPerConnection=0`, `schedule=BuildSchedule(0,0)`=빈 배열(`CounterScenario.cs:328` `decrements==0` early return). 각 워커 `RunConnectionAsync`는 증감 루프를 0회 돌지만 **루프와 무관하게 L309에서 배리어 조회(`QueryAsync`)를 반드시 1회 송신**한다. 즉 빈 실행도 연결→기준값 조회→N개 배리어 조회→최종 조회 경로를 그대로 밟는다. 배리어가 스킵되는 퇴화 경로가 코드상 존재하지 않음.

**테스트가 그 통과를 실증하는가:** 신규 `Scenario_ZeroOperations_...`(테스트 4b)가 서버 측 수신 조회 카운터로 `queryCount >= EmptyRunConnections+2`(=10)를 교차 단언(`:341`). 기준값 1 + 워커 N + 최종 1 = N+2가 정확한 하한이며, 워커가 배리어를 밟지 않으면 이 값에 못 미쳐 실패한다 — "전부 0"과 "아무것도 안 함"의 tie를 서버 수신 카운터가 깬다. `ExpectedValue==0`·`ExpectedAppliedOps==0`·`InitialSnapshot==(0,0)`·`state.Value/AppliedOps==0`까지 못 박아 "기대=실측 둘 다 우연히 0" 은폐도 차단(`:337-342` 인접). balanced 테스트는 유지됨. 용어 충돌(`:56` 주석 relabel)도 확인. **완전 해소.**

## R-X2 (Low) — 종료 테스트 단언 강화 — **해소**

기존 느슨한 이접 `captured is not null || result?.Passed == false`가 3축으로 대체됨(`CounterEndToEndTests.cs:557-572`):
- ① `queryCount >= 2` — 2회차(첫 배리어) 조회 수신 = 종료 트리거(`:516-519`)에 실제 도달. 연결·기준값 단계에서 먼저 실패하면 `queryCount<2`라 이 단언이 잡는다. 종료 경로 미실행 통과를 봉쇄.
- ② `Assert.NotNull(captured)` — PASS 반환도 결과 반환도 아닌 **예외 표면화**를 강제. 느슨한 disjunct 제거로 지적 핵심 해소.
- ③ `Assert.False(captured is TimeoutException)` — 실패가 '기한 hang'이 아니라 '연결 끊김 fast-fail'임을 판별. `OnDisconnected` 배선(`CounterScenario.cs:434-439`)이 끊기면 TimeoutException으로 떨어져 이 단언이 잡는다.
- ④ elapsed < 10s 유지.

예외 타입을 IOException으로 고정하지 않고 "TimeoutException 아님"으로 단언한 근거도 유효 — `WhenAll`이 타이밍에 따라 IOException/SocketException/ObjectDisposedException 중 하나를 표면화하므로 타입 고정은 flaky. 결과 부류 단언이 옳다. **해소.**

## R-X3 (Low) — '해당 연결만 영향' 회귀 포착 — **해소**

`healthy` 정상 연결을 **오류 주입 전**에 열고(`:377-378`) 정상 왕복 `(0,0)`으로 기준선 확인(`:379`) → 이웃 세션에 잘못된 프레임 주입·`sessionFaulted`로 서버 오류 처리 확인(`:381-390`) → **같은 `healthy` 연결**로 증감·조회 성공 `Value=1, AppliedOps=1` 단언(`:397-401`). 이전(오류 후 새 연결 생성)은 "새 연결 수립 가능"만 봤으나, 지금은 기존 이웃 연결이 오류 세션에 휩쓸려 끊기는 회귀를 실제로 포착한다. **해소.**

## R-X4 (Low) — 조회 경로 무할당 주석 정밀화 — **해소**

`PacketSendExtensions.cs`(미수정) 실제 동작과 대조:
- `SendAsync<T>`는 **항상** `ArrayPool.Rent`(`:37`) → 주석의 "버킷 비면 새 배열 할당 → 무할당 미보장"(`CounterHandler.cs:35·154`) 정확.
- Serialize 단계는 SpanWriter로 힙 할당 없음 → "이 단계 자체는 힙 할당 없다"(`:155`) 정확.
- 동기 완료(`IsCompletedSuccessfully`)는 상태머신 없이 Return(`:80-83`), 비동기는 `AwaitAndReturnAsync` 상태머신(`:88`) → "동기 무할당 가능/비동기 추가 할당 가능" 정확. `finally` 반납 보장(`:82·91`)도 정확. 인용 라인 `78-92`도 실제 범위와 일치.
- 3곳(클래스 remarks·`SendCurrentValueAsync` remarks·인라인 블록) 모두 "전체 경로 무할당" 단정 제거, "총 할당 개수 단정 안 함"으로 완화됨.
- **증감 경로(Id 3·4)를 손대지 않은 것도 옳음:** `CounterHandler.cs:105-115`는 `State.Increment()` + `ValueTask.CompletedTask`뿐 — Rent·await 없음 → "증감 경로 무할당"은 실제로 참이라 약화하면 과잉 교정. **해소.**

> [관찰, 비블로커] `PacketSendExtensions.cs:14`가 여전히 "동기 완료하면 Zero-allocation"으로 서술(원 소스 과장). 이번 판정 대상(`CounterHandler.cs:32`) 밖이고 fix notes의 [이월 관찰 1]에 정직히 기록됨. r3 별도 스코프. reverify 판정에 영향 없음.

## R-C6 (Low) — stopGate 재작성 Stop 1회 보장 — **해소, 새 레이스 없음**

`CounterEndToEndTests.cs:488-507` 게이트 구조를 코드로 검증:
- **Stop 정확히 1회:** `Interlocked.Exchange(ref stopGate,1)==0`(`:497`)로 첫 호출자만 `Task.Run(Stop)` 진입. 콜백(IO 스레드, n==2, `:519`)과 finally(테스트 스레드, `:578`)가 동시에 불러도 Exchange가 직렬화해 한 쪽만 0을 받는다. 이중 Dispose/ObjectDisposedException 원인 제거.
- **가시성 레이스 제거:** 캡처된 지역 TCS `stopCompleted`(`:491`)로 두 경로가 같은 완료 신호를 대기. 콜백이 쓴 `stopTask` 필드를 finally가 동기화 없이 읽던 옛 레이스가 구조적으로 사라짐. `Task.Run`은 동기적으로 스케줄된 뒤 `stopCompleted.Task` 반환 → await가 스케줄 이전을 관측할 창이 없음.
- **자기-Dispose 교착 회피:** `Task.Run`으로 Stop을 콜백 세션 밖 스레드로 분리(`:499`). 유효.
- **hang 없음:** `Task.Run` 본문은 `listener.Stop()` 성공 시 `TrySetResult`, 예외 시 `TrySetException`(`:502-503`) → `stopCompleted`는 항상 완료. finally의 `await BeginStopOnce()`가 무한 대기하지 않음.
- **Stop 예외 비삼킴:** teardown 버그가 finally에서 드러날 유일 기회 보존(advisor 반영). try 본문 단언은 finally 전에 이미 평가되므로, 예외가 finally에서 재던져져도 실단언 실패를 가리는 시나리오는 정상(무예외) 경로에선 발생하지 않음 — 의도된 트레이드오프.

빈 실행 테스트(4b)의 `finally { listener.Stop() }`는 콜백이 Stop을 걸지 않는 단순 단일 Stop이라 이중 Stop 무관. **해소, 신규 결함 없음.**

---

## 회귀·신규 결함 점검
- 독립 `dotnet test CounterExample.Tests` 28/28 통과(빈 실행 1건 추가분 포함). 동시성 재작성(테스트 5·7)·빈 실행 안정.
- diff 3파일 모두 예제 계층(CounterClient·CounterServer·CounterExample.Tests). ServerLib 미수정 → 의존 방향(예제→ServerLib 단방향)상 ServerLib 동작 회귀 경로 없음. ServerLib.Tests 커버리지 공백 아님(구현자 주장 코드로 재확인).
- 빌드 시 CS0419(cref 모호성) 경고는 `ServerLib/Interface/IServerListener.cs`의 **기존** XML 주석 문제로, 이번 diff가 건드리지 않은 파일 — 본 수정과 무관·비블로커.
- 제품 동작 변경은 R-X1의 Validate 완화(더 관대한 방향, 예제 코드)뿐. 기존 계약(음수·오버플로 거부) 유지.

## 판정 근거 요약
| 지적 | 상태 | 근거 |
|---|---|---|
| R-X1 | 해소 | Validate `ThrowIfNegative`로 합계 0 허용, 빈 실행이 배리어를 실제 통과(L309 무조건 조회) + queryCount≥N+2 교차 단언으로 빈 경로 통과 차단 |
| R-X2 | 해소 | 3축 단언(queryCount≥2·NotNull(captured)·¬TimeoutException)이 종료 트리거 도달·예외 표면화·fast-fail을 강제 |
| R-X3 | 해소 | 정상 연결을 오류 전에 열고 오류 후 같은 연결로 증감·조회 성공 단언 |
| R-X4 | 해소 | 주석이 실제 Rent/상태머신/finally 반납 동작과 일치, 증감 경로 무할당 단정은 참이라 유지 |
| R-C6 | 해소 | Interlocked.Exchange 게이트 + 캡처 지역 TCS로 Stop 1회 보장·가시성 레이스 제거, 신규 레이스·hang 없음 |

VERDICT: APPROVE
