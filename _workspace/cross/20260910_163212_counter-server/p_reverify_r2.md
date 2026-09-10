# Codex 프롬프트 템플릿 — 수정 후 재검토 (Phase 3 / 재검증)

---

리뷰 지적을 반영한 수정이 이루어졌다. 코드가 바뀌었으므로 이전 승인은 무효다 — 수정분과 그 파급을 다시 검토하라.

## 조정 결과 (유효 판정되어 수정 대상이 된 지적 목록)
# 31_review_adjudication_r2 — Codex 리뷰(r2) 조정 (Phase 3 승격)

Codex 재개 후 현재 코드(796aa67)를 독립 리뷰(`30_codex_review_r2.md`, meta success 152.3s). 오케스트레이터가 4건을 코드로 직접 검증해 판정한다. 이 4건은 모두 Claude 리뷰가 손대지 않은 영역의 신규 지적 → **교차 검증의 실질 가치(한 모델이 놓친 것을 다른 모델이 포착)**.

## 독립성 한계 (명시 필수)
어댑터가 정직하게 보고: 프롬프트에 포함된 `32_test_results.txt`가 Claude 지적(R-C1·C2·C3·C6·C7) 영역을 언급 → Codex는 그 5개 영역을 알고 진입했다. 따라서 **Codex가 그 영역을 clean으로 본 부분(배리어·타임아웃 정리)은 완전 독립 확인이 아니다.** 신규 지적 R-X1~X4는 그 영역 밖이라 독립성 유지. r3 재리뷰 시 test_results를 sanitize할 것.

## 판정

| # | 지적 | 심각도 | 판정 | 근거 |
|---|---|---|---|---|
| R-X1 | 계획의 '총 연산 0' 시나리오가 `Validate()` 합계≤0 거부로 실행 불가 (`CounterScenario.cs:61`) | Med | **채택** | 코드 확인: L61이 `Inc+Dec<=0` 거부. 구현자는 "순증감 0"(balanced, `CounterEndToEndTests.cs:56,256`)으로 해석했으나 Codex는 "연산 0"(빈 실행)으로 해석 — **둘 다 타당**. 해소: `Validate()`를 합계 0 **허용**으로 완화(음수는 계속 거부) + 빈 실행 E2E(Value 0·AppliedOps 0·Passed=true) 추가. balanced 테스트는 유지 |
| R-X2 | 서버 종료 테스트가 종료 경로 미실행에도 통과 가능 (`CounterEndToEndTests.cs:458`) | Low | **채택** | 모든 예외 포괄 수용 → R-C1과 같은 "빈 경로 통과" 유형. 2회차 조회 도달·종료 후 통신 실패를 신호로 단언 강화 |
| R-X3 | '해당 연결만 영향' 회귀 미포착 — 정상 연결을 오류 후 생성 (`:319`) | Low | **채택** | 오류 주입 전 정상 연결을 열어두고 오류 후 같은 연결로 증감·조회 성공을 단언하도록 강화 |
| R-X4 | 조회 경로 무할당 보장이 실제(`ArrayPool.Rent`·비동기 대기)보다 강함 (`CounterHandler.cs:32`) | Low | **채택** | 프로젝트 주석 규칙(정확한 Memory 주석)에 직접 위배. "동기 완료 시 무할당 가능, 비동기 완료 시 추가 할당 가능"으로 정밀화. 전체 경로 무할당 단정 제거 |

전건 채택. High 0 → 제품 동작 변경은 R-X1의 Validate 완화(예제 코드, 더 관대해지는 방향)뿐, 나머지는 테스트/주석.

## 하네스 입력 정정 (r2에서 발견)
- `30_review_base_r2.txt`가 HEAD로 기재됨(실제 base=b005dab). r3 전 정정.
- `30_diff_r2.patch`가 코드 경로만 포함(문서 제외) — Codex가 read-only로 리포를 직접 열어 D6 문서 갱신을 확인해 이번 검증은 성립.

## 미해결
- R-C6(test 7 stopTask 레이스, 이전 라운드 잔존) — 여전히 스코프 밖, Low. 이번 수정 라운드에서 함께 닫을지 아래 지시에 포함.

## 구현자의 수정 내역
# 32_fix_notes_r2.md — Codex 재리뷰(r2) 수정 내역

입력: `31_review_adjudication_r2.md`(유효 판정 4건 R-X1~X4 + R-C6 지시). 근거 상세는 `30_codex_review_r2.md`.
게이트: build 0오류 → CounterExample.Tests 28/28 → 전체 sln 42/42, 실패 0. 상세는 `32_test_results_r2.txt`.

수정한 제품/테스트 파일:
- `CounterClient/CounterScenario.cs`
- `CounterServer/CounterHandler.cs`
- `CounterExample.Tests/CounterEndToEndTests.cs`

기각/임의 수정 없음. 계획 밖 제품 동작 변경 없음(범위 이탈 없음). 상세는 아래.

---

## R-X1 (Med) — `Validate()` 합계 0 허용 + 빈 실행 E2E 추가

**제품 코드 (`CounterScenario.cs` `CounterScenarioOptions.Validate()`)**
- 기존: `ThrowIfLessThanOrEqual(Inc + Dec, 0, ...)` → 합계 0(빈 실행)을 거부해 확정 계획의 '총 연산 0' 시나리오가 실행 불가.
- 변경: `ThrowIfNegative(Inc + Dec, ...)`로 교체. **합계 0은 허용, 음수만 거부.**
- **삭제가 아니라 교체한 이유(중요):** `Inc + Dec`는 `int` 덧셈이다. 두 값이 각각 이미 `ThrowIfNegative`로 음수를 배제하므로 합계가 음수가 되는 유일한 경로는 **int 덧셈 오버플로**다. 검사를 통째로 삭제하면 오버플로 쌍이 `Validate()`를 통과해 `RunAsync`의 `checked` 곱셈(`CounterScenario.cs:179`)에서 `OverflowException`으로 터진다 — XML 문서가 약속한 `ArgumentOutOfRangeException` 계약 위반. `ThrowIfNegative`는 합계 0을 허용하면서도 이 오버플로 가드와 예외 타입 계약을 함께 유지한다.
- Validate()의 XML `<remarks>`에 "합계 0(빈 실행)은 유효, 음수만 거부"를 공개 검증 계약 변경으로 명시(CLAUDE.md 문서화 규칙).
- **Validate 단위 테스트:** 합계 0을 거부하던 단위 테스트는 리포에 없음(grep으로 확인) → 갱신 대상 없음.

**빈 실행이 `RunAsync` 전 경로를 정상 통과하는지 코드 추적:** `Inc=Dec=0` → `opsPerConnection=0`, `expectedValue=0`, `expectedAppliedOps=0`, `schedule=BuildSchedule(0,0)`=빈 배열. 워커는 증감 루프 0회 후 배리어 조회 1회. 최종 조회 → `Passed = 0==0 && 0==0 = true`. 배리어·최종 조회 경로는 그대로 실행됨.

**테스트 (`CounterEndToEndTests.cs`)**
- 신규 `Scenario_ZeroOperations_StillTraversesBarrierAndReportsZero` (테스트 4b). 8연결 × 증감 0회 → 실제 `CounterScenario.RunAsync` 경유.
  - **빈 경로 통과 방지(핵심):** `Value=0/AppliedOps=0/Passed=true`는 "배리어가 다 돌고 전부 0"과 "아무것도 안 함"이 구별 안 됨. balanced 테스트는 `AppliedOps`로 그 tie를 깨지만 빈 실행은 0이 진실이라 판별 카운터가 없음. → **서버 측 수신 조회 카운터**로 워커가 배리어를 실제로 밟았는지 교차 단언: 기준값 1 + 연결 수만큼 배리어 + 최종 1 = `연결 수 + 2회 이상`(`queryCount >= EmptyRunConnections + 2`).
  - 기대값 계산 자체를 못 박음: `ExpectedValue==0`·`ExpectedAppliedOps==0`·`InitialSnapshot==(0,0)`(기대=실측만 비교하면 둘 다 우연히 0일 수 있음).
- 기존 balanced 테스트(`Scenario_BalancedOperations_...`)는 **유지**(둘 다 남김).
- 용어 충돌 제거(R-X1을 낳은 원인): `CounterEndToEndTests.cs:56` 상수 주석을 "총 연산 0 시나리오" → "순증감 0(balanced)"으로 relabel하고, "총 연산 0(빈 실행)"은 신규 `EmptyRunConnections` 상수 전용으로 예약.

## R-X2 (Low) — 서버 종료 테스트가 종료 경로를 실제로 타는지 단언 (테스트 7)

- 기존 단언 `captured is not null || result?.Passed == false`는 연결·기준값 조회 단계에서 먼저 실패해도(종료 경로 미실행) 통과 가능 → "빈 경로 통과" 유형.
- 강화(3축):
  1. `Interlocked.Read(ref queryCount) >= 2` — 서버가 2회차(배리어) 조회를 받아 종료 트리거에 실제 도달했음.
  2. `Assert.NotNull(captured)` — 결과 반환이 아니라 **통신 실패(예외)로 끝남**. 느슨한 `|| result?.Passed==false` 이접(disjunct) 제거(그 느슨함이 지적의 핵심).
  3. `Assert.False(captured is TimeoutException, ...)` — 이 실패가 '기한까지 hang'이 아니라 '연결 끊김 fast-fail'임을 구분하는 실질 판별자.
  + 기존 elapsed < FailFastBudget(10s) 유지.
- **예외 타입을 IOException으로 못 박지 않은 이유:** 4×200 연산에서 `Stop()`이 도착하는 순간 일부 워커는 `SendFrameAsync` 중, 일부는 배리어 대기 중이라 `WhenAll`이 배열 순서로 첫 fault를 표면화 → 타이밍에 따라 `IOException`/`SocketException`/`ObjectDisposedException`이 올 수 있음. 타입 고정은 flaky. 대신 "TimeoutException이 **아님**"(hang이 아닌 fast-fail)이라는 결과 부류로 단언.

## R-X3 (Low) — '해당 연결만 영향' 회귀를 실제로 포착 (테스트 5)

- 기존: 정상 연결을 **오류 발생 후** 새로 생성 → "새 연결 수립 가능"만 확인. 기존에 열려 있던 이웃 연결이 오류 세션에 휩쓸려 끊기는 회귀는 미포착.
- 변경: 정상 연결(`healthy`)을 **오류 주입 전에** 열고 정상 왕복(초기 조회 `(0,0)`)으로 기준선 확인 → 잘못된 프레임으로 이웃 세션을 오류 종료시킴(`sessionFaulted` 신호로 서버가 실제 처리·거부했음을 확인) → **같은 `healthy` 연결**로 증감·조회 성공(`Value=1, AppliedOps=1`) 단언.
- 서버 측 단절 신호: 잘못된 세션은 `sessionFaulted`(`OnClientError`) 신호로 서버가 그 세션을 오류 처리했음을 이미 배리어로 확인. XML `<remarks>`에 "정상 연결을 오류 전에 여는 이유" 명시.

## R-C6 (Low, 이전 잔존) — 테스트 7 `stopTask` 게시 레이스 → Stop 1회 보장 구조

- 문제: 콜백(IO 스레드)이 쓴 `stopTask` 필드를 `finally`(테스트 스레드)가 **동기화 없이** 읽어, 가시성 지연으로 `null`을 보고 `listener.Stop()`을 한 번 더 호출 → 이중 Stop = 세션 이중 Dispose(`ObjectDisposedException`) 가능성.
- 수정: 게이트 구조로 재작성.
  - `int stopGate` + `Interlocked.Exchange(ref stopGate, 1) == 0`으로 **첫 호출자만** Stop 시작.
  - `Stop()`은 `Task.Run`으로 분리(콜백 세션 자기-Dispose 교착 회피).
  - 캡처된 지역 `TaskCompletionSource stopCompleted`로 콜백·finally 어느 쪽이든 Stop 완료를 함께 대기 → 필드 가시성 레이스 제거, `finally`가 Stop 완료를 결정적으로 대기.
  - **`Stop()` 예외를 삼키지 않음:** `try { Stop(); TrySetResult(); } catch (ex) { TrySetException(ex); }` — 리스너 teardown 버그가 이 `finally`에서만 드러날 유일한 기회를 지킴(advisor 지적 반영, r1 증거상 Stop은 throw하지 않음).
- 결과: Stop()이 콜백·finally 어느 경로에서도 **정확히 1회** 실행됨. 결정성(응답 드롭)과 fast-fail(연결 끊김)은 그대로.

## R-X4 (Low) — 조회 경로 무할당 주석 정밀화 (`CounterHandler.cs`, 3곳)

- 근거: `PacketSendExtensions.SendAsync`는 항상 `ArrayPool.Rent`를 수행한다. `Rent`는 해당 버킷이 비어 있으면 새 배열을 할당(요청보다 큰 크기 반환 가능)하므로 "동기 완료 = 무할당"은 과한 단정. 하위 `SocketPipelineSession.SendAsync`/`SendAllAsync`·`AwaitAndReturnAsync`에도 비동기 대기 할당 경로 있음.
- 3곳 모두 정밀화:
  1. 클래스 `<remarks>` 조회 응답 경로 `<item>`(~30-36): "풀에 재사용 버퍼가 있고 동기 완료 시 추가 할당 없이 반환 **가능**, 단 버킷이 비면 Rent가 새 배열 할당 → **무할당 미보장**, 비동기 완료 시 상태머신·하위 송신 경로에서 **추가 할당 가능**, 이 경로 전체 할당 개수 **단정 안 함**". `finally` 반납 보장은 유지. 참조를 `78-88` → `78-92`로 정정.
  2. `SendCurrentValueAsync` `<remarks>`(~127-137): 동일 취지로 "동기 완료 시 무할당" 단정 제거, "총 할당 개수 단정 안 함"으로 완화.
  3. `SendCurrentValueAsync` 인라인 `//` 블록(~146-152): ① Rent가 버킷 비면 새 배열 할당(무할당 미보장) ② Serialize 단계 자체만 힙 할당 없음 ④ 동기/비동기 반납 보장·총 할당 단정 안 함으로 수정. "상태머신 1개" 정확 개수 단정 제거.
- **의도적으로 손대지 않은 2곳:**
  - **증감 경로(Id 3·4, `CounterHandler.cs` line 105 부근)**: `Rent`도 `await`도 없어 "증감 경로 전체가 무할당"은 **실제로 참** → 약화하면 과잉 교정이라 유지.
  - **`PacketSendExtensions.cs`(ServerLib, line 14-16·31의 동일 과장)**: 명시 대상(CounterServer/CounterHandler.cs) 밖 + r2 리뷰 diff 밖 → 이번 라운드에서 수정하면 범위 이탈. 아래 [이월 관찰]에 기록.

---

## [이월 관찰] (이번 라운드 비수정 — r3 참고)

1. **`ServerLib/Core/Serialization/PacketSendExtensions.cs`의 무할당 주석 과장(line 14-16, 31)** — R-X4가 지적한 "조회 경로 무할당" 단정의 실제 근원. `Rent`가 버킷 비면 할당하는데 "동기 완료 시 Zero-allocation"이라 서술. CounterHandler 쪽 인용 주석은 이번에 정밀화했으나 **원 소스는 ServerLib라 명시 대상·리뷰 diff 밖**이라 미수정. ServerLib 주석 정확화는 별도 스코프에서 다뤄야 함(제품 라이브러리 변경).
2. **`plan/contention_counter_0910.md:94`의 테스트 수 스냅샷("E2E 8 / 합계 26")** — 계획 작성 시점 스냅샷이며 r1/r2 라운드가 테스트를 추가하며 이미 stale(실제 E2E 9 → 신규 빈 실행 포함 10, 합계 28). 이 줄은 검증 계약이나 시나리오 sum>0 제약을 서술하지 않고 **역사적 계획 스냅샷의 테스트 개수**일 뿐이라, 라운드마다 갱신하지 않는 방침으로 미수정(D6 문서 갱신 관례 밖). r3에서 계획 문서 현행화를 원하면 이 줄 포함.

## 검증 요약
- `dotnet build ClaudeCodeStudy.sln -c Release`: 0오류 0경고.
- `dotnet test CounterExample.Tests`: 28/28 통과(27→28, 빈 실행 1건 추가).
- `dotnet test ClaudeCodeStudy.sln`: 42/42 통과(EchoExample.Tests 14 + CounterExample.Tests 28), 실패 0.
  - **ServerLib.Tests(210건)가 이 sln에 없는데 커버리지 공백 아님:** 이번 라운드 수정 파일 3개는 모두 CounterClient·CounterServer·CounterExample.Tests(예제 계층)뿐 **ServerLib 자체는 미수정**. 의존 방향은 예제 → ServerLib 단방향(역방향 금지)이라 이 diff가 ServerLib 동작에 영향을 줄 수 없다 → ServerLib.Tests가 이 변경으로 깨질 경로가 없음.
- races 대비 CounterExample.Tests 누적 5회 그린. 원문은 `32_test_results_r2.txt`.
- git commit·`.git/auto_commit_msg.txt` 생성 안 함(하네스 규칙: 커밋은 메인 세션 Stop 훅 책임. CLAUDE.md의 auto_commit_msg 규칙은 이 태스크에서 오버라이드됨).

## 수정 후 diff와 테스트 결과
- 수정 diff: _workspace/cross/20260910_163212_counter-server/32_diff_r2fix.patch 파일을 직접 읽어라.
- 테스트 결과: # 32_test_results_r2.txt — 리뷰 수정 라운드 r2 테스트 실행 원문

대상: Codex 재리뷰(r2) 유효 판정 4건(R-X1~X4) + R-C6 반영 후 검증.
게이트: dotnet build → dotnet test CounterExample.Tests → dotnet test 전체 sln, 전량 통과·실패 0.
races(테스트 5·7·신규 빈 실행) 대비로 CounterExample.Tests를 추가 반복 실행.

수정된 산출물 파일:
- CounterClient/CounterScenario.cs          (R-X1: Validate 합계 0 허용, 음수만 거부)
- CounterServer/CounterHandler.cs            (R-X4: 조회 경로 무할당 주석 정밀화 3곳)
- CounterExample.Tests/CounterEndToEndTests.cs (R-X1 빈 실행 E2E 추가·상수 relabel, R-X2/R-C6 테스트7 재작성, R-X3 테스트5 강화)

════════════════════════════════════════════════════════════════════════════
[1] dotnet build ClaudeCodeStudy.sln -c Release
════════════════════════════════════════════════════════════════════════════
빌드했습니다.
    경고 0개
    오류 0개
경과 시간: 00:00:03.05

════════════════════════════════════════════════════════════════════════════
[2] dotnet test CounterExample.Tests/CounterExample.Tests.csproj -c Release   (최초, 빌드 포함)
════════════════════════════════════════════════════════════════════════════
  ServerLib -> ...\ServerLib.dll
  CounterClient -> ...\CounterClient.dll
  CounterServer -> ...\CounterServer.dll
  CounterExample.Tests -> ...\CounterExample.Tests.dll
지정된 패턴과 일치한 총 테스트 파일 수는 1개입니다.

통과!  - 실패:     0, 통과:    28, 건너뜀:     0, 전체:    28, 기간: 1 s - CounterExample.Tests.dll (net10.0)

  (27 → 28: 신규 빈 실행 테스트 Scenario_ZeroOperations_StillTraversesBarrierAndReportsZero 1건 추가)

════════════════════════════════════════════════════════════════════════════
[3] dotnet test ClaudeCodeStudy.sln -c Release   (전체 sln)
════════════════════════════════════════════════════════════════════════════
  ServerLib -> ...\ServerLib.dll
  CounterServer -> ...\CounterServer.dll
  CounterClient -> ...\CounterClient.dll
  EchoExample.Tests -> ...\EchoExample.Tests.dll
  CounterExample.Tests -> ...\CounterExample.Tests.dll
지정된 패턴과 일치한 총 테스트 파일 수는 1개입니다.
지정된 패턴과 일치한 총 테스트 파일 수는 1개입니다.

통과!  - 실패:     0, 통과:    14, 건너뜀:     0, 전체:    14, 기간: 98 ms - EchoExample.Tests.dll (net10.0)
통과!  - 실패:     0, 통과:    28, 건너뜀:     0, 전체:    28, 기간: 1 s - CounterExample.Tests.dll (net10.0)

  (sln 내 테스트 프로젝트는 EchoExample.Tests·CounterExample.Tests 2개. ServerLib.Tests는 이 sln에 포함되지 않음.)
  합계: 42/42 통과, 실패 0.

════════════════════════════════════════════════════════════════════════════
[4] CounterExample.Tests 반복 실행 (races 대비, --no-build)
════════════════════════════════════════════════════════════════════════════
=== RUN 1 ===
통과!  - 실패:     0, 통과:    28, 건너뜀:     0, 전체:    28, 기간: 1 s - CounterExample.Tests.dll (net10.0)
=== RUN 2 ===
통과!  - 실패:     0, 통과:    28, 건너뜀:     0, 전체:    28, 기간: 1 s - CounterExample.Tests.dll (net10.0)
=== RUN 3 ===
통과!  - 실패:     0, 통과:    28, 건너뜀:     0, 전체:    28, 기간: 1 s - CounterExample.Tests.dll (net10.0)

CounterExample.Tests 누적 5회 실행([2]+[3]+[4]×3) 전부 28/28 통과 — 테스트 5·7·신규 빈 실행 안정.

════════════════════════════════════════════════════════════════════════════
게이트 판정: PASS
- build 0오류 0경고
- CounterExample.Tests 28/28 (5회 연속 그린)
- 전체 sln 42/42 (실패 0)

## 지시
1. 유효 판정된 각 지적이 실제로 해소되었는지 코드로 확인하라 (주장이 아니라 diff 근거로).
2. 수정이 만든 새로운 결함·회귀가 없는지 수정 주변 코드를 확인하라.
3. 테스트 결과가 수정 범위를 커버하는지 판정하라.

마지막 줄에 `VERDICT: APPROVE` 또는 `VERDICT: REQUEST-CHANGES`(사유 번호 목록 첨부)를 출력하라. 한국어로 작성하고 파일을 수정하지 마라.
