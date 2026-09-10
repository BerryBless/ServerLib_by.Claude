# 33_claude_reverify — 수정 재검토 (Claude 측, mode=reverify, 저하 모드)

- **리뷰어:** cross-reviewer (Claude) — 구현자와 분리된 독립 시각
- **저하 모드:** Codex 토큰 부족으로 Codex 재검토 스킵. 본 재검토는 **Claude 단독**이다.
- **입력:** `31_review_adjudication.md`, `32_fix_notes.md`, `32_diff.patch`, `32_test_results.txt`, 이전 `30_claude_review.md`.
- **방침:** 코드가 바뀌었으므로 이전 승인은 인용하지 않고 **변경분 전체를 다시** 봤다. 구현자 주장이 아니라 **리포의 실제 코드**로 각 지적의 해소를 확인했다.
- **직접 실행:** `dotnet build CounterExample.Tests -c Release`(0오류, CS0419 10건=기존·diff 밖), 수정 대상 2개 테스트 단독 실행(2/2 통과), `CounterExample.Tests` 전체 **3회 반복**(27/27 × 3, flaky 미관측). 구현자의 5× + 전체 sln 41/41과 합쳐 회귀 없음 교차 확인.
- **커밋 상태:** 수정은 `796aa67`로 이미 커밋, 워킹트리 깨끗. `git diff 5c949ae..HEAD --stat`로 **변경 집합 전체를 열거**해 확인: 제품 파일은 정확히 `CounterServer/CounterHandler.cs`·`CounterClient/CounterScenario.cs`·`CounterExample.Tests/CounterEndToEndTests.cs` 3개뿐이고(내용이 `32_diff.patch`와 일치), 그 외 `cd89ecd`의 `invoke-codex.ps1`은 **하네스 스크립트(제품 비범위)**다. **계획 밖 제품 파일 변경 0건**(스코프 크리프 없음).
- **전체 sln 41/41·EchoExample 14/14는 `32_test_results.txt`의 구현자 측정치**다(내가 직접 실행한 것은 `CounterExample.Tests`뿐). 변경된 세 파일 중 어느 것도 `EchoExample.Tests`에서 도달 불가하므로 회귀 표면 없음.

---

## 1. 유효 판정 지적별 재검토 (코드 근거)

### R-C1 — 무응답 타임아웃 테스트 실질화 → **해소(RESOLVED)**
개편된 test 8(`CounterEndToEndTests.cs:499-587`)이 **실제로 워커 in-flight 취소·정리 경로를 탄다**는 것을, 구현자 주장이 아니라 `CounterScenario.RunAsync`의 제어 흐름으로 직접 역추적해 확인했다:

- 서버 콜백(`:516-528`): `packetId == CounterQueryPacket.Id && Interlocked.Increment(ref queryCount) >= 2` → **2회차 이후 조회만 드롭**, 증감·1회차(기준값) 조회는 실제 `handler.HandleAsync`가 처리. `>=2` 조건의 `Interlocked.Increment`는 조회 패킷에만 단락 평가되어 증감은 카운터를 건드리지 않음(확인).
- 흐름: ① 기준값 조회 `:212`가 **워커 생성(`:221`) 전에** 완료(await) → 서버 queryCount=1(handled)로 확정, 순서 결정적. ② 워커 출발 → 각 40연산 송신 → `:300` 배리어 조회 송신(2·3회차 = 드롭). ③ 두 워커가 `QueryAsync`의 `await waiter.Task.WaitAsync(ct)`(`CounterScenario.cs:475`)에서 정지. ④ 1s `deadlineCts` 발화 → `ct` 취소 → `:475`가 `OperationCanceledException` → `catch`에서 대기자 회수 후 재던짐(`:477-483`) → `Task.WhenAll(workers)` fault → `ThrowIfDeadlineAsync`(`:354-357`)가 OCE를 **`TimeoutException`("경합 카운터 시나리오가 기한")으로 변환** → `finally`(`:257-267`) 전 연결 `DisposeAsync`.
- **3중 단언이 이 경로를 고정한다:** ① `timeout.Message`에 RunAsync 고유 문구 포함(`:566`) = 테스트 안전망(프레임워크 기본 메시지)이 아니라 **RunAsync 내부 기한 경로**가 낸 것임을 판별. ② `queryCount >= 1 + Connections(=3)`(`:574`) = 워커가 증감 전량 송신 후 배리어까지 도달했다는 **직접 증거**(기존 테스트면 1에서 막혀 실패). ③ `disconnected == Connections`(`:580`) = 정리 누락 없음.
- **타이밍 마진:** 루프백 40연산은 ms 단위, 기한 1s → 배리어 도달이 기한보다 압도적으로 빠름. OpsEachWay를 키우지 않은 선택(취소점은 배리어 대기이지 송신이 아님)도 타당. 3× + 구현자 5×에서 flaky 미관측.
- **근거 한계:** 문구 단언 한 가지 — 내부 기한 예외의 두 발생처(`:255` 바깥 catch, `:357` 헬퍼)가 **동일 문자열**이라 "둘 중 어느 쪽"까지는 구별 못 한다. 그러나 배리어 대기 취소는 반드시 `:357`을 경유하므로(`WhenAll`이 `ThrowIfDeadlineAsync`로 감싸여 있음) 경로 판별에는 충분하다. 구현자도 이 한계를 정직하게 명시. **문제 아님.**

### R-C2 — 핸들러 방어 코드 직접 호출 테스트 → **해소(RESOLVED)**
신규 test 9(`:609` `Handler_DirectCall_RejectsFramesUnreachableViaTransport`)가 두 미도달 분기를 **실제로 커버**함을 핸들러 메시지 포맷과 대조해 확인:

- (a) 길이 교차 검증 분기: `lengthMismatch = new byte[HeaderSize]`(4B), `WriteHeader(IncrementPacket.Id, bodyLength:4)` → `TryParseHeader`가 bodyLength=4, frame.Length=4 → `4 != HeaderSize(4)+4`(`CounterHandler.cs:95`) 발화 → 메시지 `"선언 4B, 실제 0B (Id=3)"`. 단언 `Assert.Contains("선언 4B, 실제 0B", …)` 일치.
- (b) 조회(Id=18) `RequireBodySize` 분기: `queryWrongBody = new byte[HeaderSize+4]`(8B), `WriteHeader(CounterQueryPacket.Id, bodyLength:4)` → 길이 교차 검증 통과(8==4+4) → `case 18`의 `RequireBodySize(actual:4, expected:CounterQueryPacket.BodySize)`. **`CounterQueryPacket.BodySize == 0` 확인**(`CounterQueryPacket.cs:39`) → 메시지 `"Id=18는 0B여야 하는데 4B입니다"`. 단언 일치.
- `session: null!`이 역참조되지 않음을 확인: (a)는 switch 진입 전, (b)는 `SendCurrentValueAsync`(session 최초 사용) **도달 전** `State`를 건드리기도 전에 throw. 스텁 불필요.
- 각 경로의 **구별 본문**까지 단언해 경로 뒤바뀜 회귀를 잡음(두 경로 같은 접두사 `InvalidBodyMessagePrefix` 공유). 적절.
- `PacketPool.TryParseHeader`·`WriteHeader`·`HeaderSize`가 전부 public static임을 확인(테스트가 트랜스포트 없이 프레임 수제작 가능). **문제 아님.**

### R-C3 — 미사용 Serializer 필드 제거 + remarks 교정 → **해소(RESOLVED)**
- `CounterHandler.cs`에서 `private static readonly BinaryPacketSerializer Serializer` 필드와 선언 주석 2행이 **삭제됨**(현재 파일에 부재 확인). 세 ID(3·4·18) 본문이 전부 0B라 역직렬화 경로가 없어 사문화 상태였던 것 맞음.
- `HandleAsync`의 `<remarks>`(`:78-81`)가 "**역직렬화 자체를 하지 않고** 길이·ID 검증만으로 판별"로 교정됨 — 실제 동작과 일치. 기존 "Deserialize에 넘기기 전에"의 부정확성 해소.
- **`using ServerLib.Core.Serialization;` 유지 결정은 타당(구현자 판단 지지).** 이 네임스페이스에 `session.SendAsync(response)`를 제공하는 확장 메서드 `PacketSendExtensions.SendAsync`가 들어 있음을 확인(`PacketSendExtensions.cs:5,33`). **리뷰어(나)의 원래 수정안 "using도 삭제"는 오류였고**(CS 컴파일 오류 유발), 구현자가 이를 바로잡은 것이다. 빌드 0오류로 실증. 이는 조치 확대가 아니라 제안의 명백한 오류 회피 — **정당**.

### R-C7 — 미완료 조회 1건 불변식 명문화 → **해소(RESOLVED, 문서)**
- `CounterScenario.cs`의 `CounterConnection.QueryAsync` `<remarks>`(`:454-459`)에 불변식 주석 추가. 내용이 실제 흐름과 정확히 일치: 포기된 조회(취소·송신 실패로 `:482` `catch`에서 대기자 회수)가 있는 연결 재사용 시 지연 응답이 새 대기자를 낡은 스냅샷으로 완료시킬 수 있음 → "실패·취소 연결은 반드시 `DisposeAsync`, 재사용 금지". `RunAsync`가 실패 시 `finally`에서 폐기해 이를 지킴도 명시.
- 원 지적이 "현재 흐름 도달 불가"였으므로 코드 변경 없이 불변식 주석으로 닫는 것이 적절. 범위를 `CounterScenario.cs` 한 곳으로 한정한 것도 조정/리뷰어 인용("해당 코드" 단수)과 일치. **문제 아님.**

---

## 2. 미수정 항목의 타당성 검토

### R-C6 — test 7 `stopTask` 게시 레이스: **좁은 조치 + 의도적 잔존, 스코프 결정 타당(단, 주의 기록)**
- **적용된 조치:** 개편된 test 8은 `listener.Stop()`을 `finally` **단 한 곳**에서만 호출(`:584-585`), 콜백에서 Stop 미호출 → 이중 Stop 불가. 조정(adjudication)이 R-C6를 "R-C1 테스트 개편 시 Stop 1회 보장"으로 스코프한 **문자 그대로의 요구는 충족**.
- **그러나 — 내가 R-C6에서 실제 인용한 결함은 test 7이다.** `30_claude_review.md`의 R-C6가 인용한 라인 `408/420/468`은 test 7 `Scenario_ServerStopsMidRun`의 코드다. 현재 파일에서 확인: `Task? stopTask = null`(`:408`, 평문 지역) → 콜백(IO 스레드)이 `stopTask = Task.Run(listener.Stop)`(`:420`, 평문 쓰기) → `finally`가 `if (stopTask is not null) await stopTask; else listener.Stop();`(`:468-469`, 테스트 스레드 읽기). **이 게시(publication) 레이스는 이번 라운드에서 손대지 않았다.** IO 스레드의 평문 쓰기와 테스트 스레드의 읽기 사이에 보장된 happens-before가 없어, 약한 메모리 모델(ARM)에서는 이론적으로 `finally`가 stale `null`을 읽어 두 번째 `Stop()`을 호출 → 세션 이중 Dispose → `ObjectDisposedException` 가능.
- **판정: 스코프 결정은 타당하며 병합 블로커 아님.** 근거: (1) 원 심각도 Low·**재현 미확인**·이론적, x64 TSO에서 사실상 순서 보장, 5×+3× 미발현. (2) 조정이 R-C6를 test 8 범위로 못 박았고, 이번 지시는 "유효 판정된 지적만 수정"이므로 test 7의 미지시 변경은 하네스 범위 규칙 위반. 구현자가 이를 정직하게 **잔존으로 보고**하고 수정안(`Interlocked` 멱등 `Stop` + `Volatile`/`Interlocked` 게시)까지 제시한 처리는 **올바르다**.
- **주의(오케스트레이터 판단 필요):** 조정의 R-C6 스코핑이 **내가 지적한 코드(test 7)를 실제로 닫지는 못했다**. test 7의 레이스를 닫으려면 별도 지시가 필요하다. 다만 Low·이론적·x64-safe이므로 후속으로 미뤄도 무방. **이 불일치는 코드 결함이 아니라 조정 스코핑의 정밀도 문제이며, 승인을 막지 않는다.**

### R-C3 `using` 유지: **타당(위 §1 R-C3에서 검토 완료).** 리뷰어 제안의 컴파일 오류를 피한 정당한 판단.

---

## 3. 새 결함·회귀 점검

- **제품 동작 변경 없음.** 제품 코드 변경은 `CounterHandler.cs`의 **미사용 필드 1개 삭제**뿐(동작 불변). 나머지는 테스트/주석.
- **회귀 없음.** 빌드 0오류(CS0419 10건은 전부 `ServerLib/Interface/IServerListener.cs`의 **기존** 경고, diff 밖 — 회귀 아님, 이전 리뷰 §4.3과 동일). `CounterExample.Tests` 27/27 × 3 + 구현자 5×, 전체 sln 41/41, `EchoExample.Tests` 14/14 유지.
- **test 8 개편이 새 결함을 만들지 않음:** state 값 단언 없음(증감이 실제 적용되어도 뒤집힐 단언 부재 확인), 기준값 조회 순서 결정적, queryCount 카운팅이 조회에만 단락 평가되어 증감과 무간섭, 타이밍 마진 충분.
- **test 9는 타이밍 비의존**(순수 직접 호출) → flaky 표면 없음.

---

## 4. 미해결 승계 (최종 보고로)
- **Phase 3 교차 검증 부재** — 본 재검토는 Claude 단독. Codex 재개 후 재평가 여지.
- **R-C4/R-C5 설계 문서 문구 정정(오케스트레이터 의무)** — 조정이 R-C4(기각)·R-C5(전체취소 부분채택)를 "설계 문서 문구만 최종 보고에 정정 기록"으로 넘겼다. 이는 구현자가 아니라 **최종 보고의 문서 의무**이며 이번 라운드에 기록되지 않았다. 베이스 §5의 "송신 타임아웃(블로킹 전제)"·"실패 시 전체 시나리오 취소" 문구를 실제 코드("전체 기한 하나로 수렴")에 맞게 설계 문서에서 정정해야 한다 — 이 승계 목록이 핸드오프로 쓰이면 누락되지 않도록 명시.
- **test 7 `stopTask` 게시 레이스** — Low·이론적·x64-safe. 조정 스코프 밖으로 의도적 잔존. 닫으려면 오케스트레이터의 별도 지시 필요(구현자 제안 수정안 존재). 승인 블로커 아님.
- R-C1 "배리어 필요성 결정적 증명 불가"(flaky 없이는) 한계 — 설계 근거로만 성립(Phase 1 F-3 동일).

---

## 종합
유효 판정된 5개 지적(R-C1·C2·C3·C6·C7)을 **코드·diff·독립 실행으로** 재확인했다. R-C1(워커 in-flight 취소 경로 실질화)·R-C2(방어 분기 직접 커버)·R-C3(dead 필드 제거+remarks 교정)·R-C7(불변식 명문화)은 **실제로 해소**됐고, R-C3의 `using` 유지는 리뷰어 원안의 컴파일 오류를 바로잡은 **정당한** 판단이다. R-C6는 test 8 범위에서 Stop 1회를 보장해 조정 요구를 충족했으나, 내가 원래 인용한 **test 7의 `stopTask` 게시 레이스는 의도적으로 잔존**한다 — 이는 Low·이론적·x64-safe이고 하네스 범위 규칙에 따른 정직한 스코프 결정이므로 **승인을 막지 않으며**, test 7을 닫을지는 오케스트레이터가 별도로 판단하면 된다. 수정은 제품 동작을 바꾸지 않았고 회귀도 없다(빌드 0오류, 41/41, 반복 실행 flaky 미관측).

VERDICT: APPROVE
