# 32_fix_notes — 리뷰 수정 라운드 (구현자)

- **입력:** `31_review_adjudication.md`(유효 판정), 근거는 `30_claude_review.md`의 [R-C#] 항목, `20_impl_notes.md`(기존 맥락).
- **저하 모드:** Codex 토큰 부족으로 이번 리뷰는 Claude 단독. 수정·재테스트는 정상 수행.
- **범위:** 전부 테스트/주석/dead code — **제품 동작 변경 없음**. 기각된 R-C4/R-C5(전체취소)/취향 5건은 건드리지 않았다.
- **검증:** build(0오류/0경고) → CounterExample.Tests 5× 27/27 → 전체 sln 41/41, 실패 0. 상세는 `32_test_results.txt`.
- **커밋:** 하지 않음(메인 세션 Stop 훅 책임). `.git/auto_commit_msg.txt` 생성 안 함.

---

## 지적별 수정 내역

### R-C1 (채택·수정) — 무응답 타임아웃 테스트 실질화
**파일:** `CounterExample.Tests/CounterEndToEndTests.cs` (test 8 `Scenario_UnresponsiveServer_TimesOutAndCleansUp`, 및 XML 문서)

- **문제:** 기존 테스트는 서버가 **모든** 조회에 무응답이라, `RunAsync`가 **기준값 조회(`:212`)에서 먼저** 막혀 워커 배열이 채워지지도 않은 채 기한이 발화했다. 즉 계획이 요구한 "실제 작업 취소" 경로(워커 in-flight 취소)가 **전혀 실행되지 않았다**(리뷰어 프로브: `Increment=0`).
- **수정:** 서버 콜백을 "**1회차(기준값) 조회만 실제 `CounterHandler`로 정상 응답**하고, 증감은 정상 처리하며, **2회차 이후(배리어) 조회는 드롭**"으로 재구성했다.
  - 그 결과 워커들이 출발 → 증감 전량 송신 → 배리어 조회 응답을 기다리며 멈춘 **in-flight 상태**에서 기한이 발화 → `deadlineCts` 취소 → 워커의 `await waiter.Task.WaitAsync(ct)` 취소 → `finally` 연결 폐기라는 **실제 취소·정리 경로**가 실행된다.
  - `OpsEachWay = 20`(증감 각 20회, 연결 2개): 루프백에서 1초 기한보다 훨씬 빨리 배리어에 도달. 송신 구간이 아니라 "배리어 대기"가 취소 지점이므로 연산 수를 키우지 않았다(어드바이저 지적 반영 — 연산을 키우면 마진만 줄어든다).
  - 서버는 **드롭 시에도 소켓을 계속 읽는다**(콜백이 `CompletedTask` 반환). 리스너를 멈추거나 `OnReceived`를 미등록하는 식으로 "단순화"하지 않았다 — 그러면 R-C4(커널 흐름 제어 블로킹, 기각·비범위)를 되살린다(어드바이저 확인).
- **정리(teardown) 검증 — 경로를 3중으로 고정:**
  1. **취소 경로 판별(어드바이저 지적 반영):** `Assert.ThrowsAsync`가 돌려준 `TimeoutException`을 바인딩해 `Message`가 RunAsync 고유 문구(`"경합 카운터 시나리오가 기한"`)를 포함하는지 단언. 이로써 이 예외가 **RunAsync 내부 기한 경로**(배리어에서 취소 → `ThrowIfDeadlineAsync`, `CounterScenario.cs:357`)에서 나온 것이지, RunAsync가 매달려 테스트의 15s `WaitAsync` 안전망(프레임워크 기본 메시지)이 낸 것이 아님을 판별한다 — 단순 hang과 실제 취소·정리를 구분.
     - **메시지 중복 주의:** 내부 기한 예외의 두 발생처(`:255` 바깥 catch, `:357` 헬퍼)는 **동일 문자열**이다(리뷰 [취향-1]이 지적한 중복). 따라서 이 단언은 "두 내부 발생처 중 어느 쪽"까지는 구별하지 못하고, "**RunAsync 내부 기한 경로 vs 테스트 안전망**"을 구별한다(배리어 대기 취소는 `:357`을 경유한다).
  2. **워커 배리어 도달:** `Assert.True(Interlocked.Read(ref queryCount) >= 1 + Connections)` — 기준값 1 + 배리어 N. 통과 = 워커가 증감 전량 송신 후 배리어 조회까지 보냈다는 직접 증거. 기존 테스트였다면 `queryCount=1`에서 막혀 실패했을 것이다.
  3. **정리 누락 없음:** 기존 `OnClientDisconnected` 연결 수 관측(`disconnected == Connections`) 유지.
- **state 역단언 주의(어드바이저 지적):** 이제 증감이 실제로 서버에 적용되므로 `state.Value/AppliedOps==0` 같은 잔존 단언이 있었다면 뒤집혔을 것 — 개편 테스트는 `state` 값을 단언하지 않으므로 해당 없음(확인 완료).

### R-C2 (채택·수정) — 핸들러 방어 코드 직접 호출 테스트 추가
**파일:** `CounterExample.Tests/CounterEndToEndTests.cs` (신규 test 9 `Handler_DirectCall_RejectsFramesUnreachableViaTransport`)

- **문제:** (a) `CounterHandler.cs`의 프레임 길이 교차 검증 분기와 (b) 조회(Id=18)의 `RequireBodySize` 분기는 프레이밍 계층이 항상 `frame.Length == HeaderSize + bodyLength`를 보장하므로 **트랜스포트 경유로는 영구 미도달**.
- **수정:** 소켓을 경유하지 않고 `CounterHandler.HandleAsync`를 **직접** 호출하는 단위 테스트 1개 추가:
  - (a) 헤더가 `IncrementPacket.Id`/본문 4B를 선언했지만 실제 버퍼는 헤더 4B뿐 → `frame.Length(4) != HeaderSize(4)+4` → 길이 교차 검증 분기 발화.
  - (b) `CounterQueryPacket.Id`(18)/본문 4B(실 프레임 8B) → 길이 교차 검증은 통과, `case 18`의 `RequireBodySize(expected:0, actual:4)`가 발화.
  - `session`은 `null!`로 넘긴다: 두 경로 모두 `CounterState`를 건드리기 전, 조회 응답 송신(session 사용)에 도달하기 전에 예외를 던지므로 역참조되지 않는다(스텁 불필요).
  - **메시지 판별(어드바이저 지적):** 두 경로가 같은 접두사(`InvalidBodyMessagePrefix`)를 쓰므로, 경로가 뒤바뀌는 회귀를 잡기 위해 각 경로의 구별되는 본문까지 단언 — (a) `"선언 4B, 실제 0B"`, (b) `"Id=18는 0B여야 하는데 4B입니다"`. `ThrowsAsync`로 동기 throw·faulted task 양쪽을 포괄.

### R-C3 (채택·수정) — 미사용 Serializer 필드 제거 + remarks 교정
**파일:** `CounterServer/CounterHandler.cs`

- **필드 제거:** `private static readonly BinaryPacketSerializer Serializer = new();`와 선언 주석 2행을 삭제. 세 처리 ID(3·4·18)의 본문이 전부 0B라 역직렬화가 없어 이 필드는 사문화 상태였다.
- **`using` 관련 정정(리뷰어 수정안 일부 미채택 — 근거 있음):** 리뷰어 R-C3 수정 방향은 "`using ServerLib.Core.Serialization;`도 삭제"라고 했으나, **이 네임스페이스에는 `session.SendAsync(response)`를 제공하는 확장 메서드 `PacketSendExtensions`가 들어 있다**(`PacketSendExtensions.cs:5 namespace ServerLib.Core.Serialization;`). using을 지우면 CS 컴파일 오류가 난다. 따라서 **using은 유지**하고 필드만 제거했다(빌드 0오류로 확인). 이는 "조치 확대"가 아니라 리뷰어 제안의 명백한 오류 회피다.
- **remarks 교정:** 클래스가 아니라 `HandleAsync`의 `<remarks>` 문구를 실제 동작에 맞게 고쳤다. 기존 "`Deserialize`에 넘기기 전에 ID와 길이를 직접 확인해야"는 이 핸들러가 `Deserialize`를 **호출하지 않으므로** 부정확했다. 교정 후:
  > 세 ID(3·4·18)는 본문이 모두 0B이므로 **역직렬화 자체를 하지 않고** 길이·ID 검증만으로 프레임을 판별합니다. 본문이 있는 패킷을 추가할 때도 이 검증이 선행되어야 합니다 — `Deserialize<T>`는 헤더를 건너뛰기만 할 뿐 타입 ID 일치·본문 전체 소비를 검사하지 않아 …

### R-C6 (보고 — 아래 단서와 함께 좁게 처리)
**파일:** `CounterExample.Tests/CounterEndToEndTests.cs`

- **적용한 조치:** 개편된 test 8은 **콜백에서 `Stop()`을 부르지 않는다**. `listener.Stop()` 호출 지점은 `finally` **단 한 곳**이며, 따라서 **정확히 1회** 호출된다(해당 `finally`에 주석 명시). 이것이 조정(adjudication)이 R-C6를 "R-C1 테스트 개편 시 함께 정리(Stop 1회 보장)"로 스코프한 바를 충족한다.
- **미해결로 보고하는 부분(정직한 스코프 한계):** 리뷰어 R-C6가 인용한 라인 `408/420/468`은 **test 7 `Scenario_ServerStopsMidRun`의 `stopTask`**이다(test 8이 아님). test 7에는 콜백(IO 스레드)의 `stopTask = Task.Run(listener.Stop)` 쓰기와 `finally`의 읽기 사이에 게시(publication) 레이스가 남아 있어, 이론적으로 `listener.Stop()`이 두 스레드에서 겹칠 수 있다(리뷰어도 "재현 미확인·이론적"이라고 명시).
  - 이번 라운드 지시는 "**유효 판정된 지적만 수정**"이며, 조정은 R-C6를 **R-C1 테스트 개편 범위로** 못 박았다. test 7의 `stopTask` 레이스를 이번에 손대는 것은 **조정이 스코프하지 않은 테스트에 대한 미지시 변경**이므로, 하네스의 범위 규칙에 따라 **수정하지 않고 여기 보고만** 한다.
  - 만약 오케스트레이터가 test 7의 레이스도 이번에 닫아야 한다고 판단하면, 그것은 별도 지시(또는 [범위 이탈] 처리) 사항이다. 제안 수정안(참고): `Interlocked` 가드로 `Stop()`을 멱등화(`StopOnce`)하고 `stopTask`를 `Volatile`/`Interlocked`로 게시 후 `finally`에서 await — 동작 변경 없이 이중 `Stop` 가능성을 제거.

### R-C7 (채택·수정) — 미완료 조회 1개 불변식 주석 명문화
**파일:** `CounterClient/CounterScenario.cs` (`CounterConnection.QueryAsync`의 `<remarks>`)

- 조정/리뷰어가 지정한 "해당 코드"(단수)는 `CounterScenario.cs`의 `QueryAsync`이다. 여기 `<remarks>`에 불변식을 추가:
  > **[불변식 — 포기된 조회가 있는 연결은 재사용하지 않는다]** 응답 매칭은 요청 ID 없이 "연결당 미완료 조회 1건" 전제에만 의존한다. 조회를 in-flight 상태로 포기한(취소·송신 실패로 `catch`에서 대기자를 회수한) 연결을 다시 조회에 쓰면, 그 사이 도착한 이전 요청의 **지연 응답**이 새 대기자를 **낡은 스냅샷**으로 완료시킬 수 있다. 실패·취소로 포기한 연결은 반드시 `DisposeAsync`해야 하며 재사용하지 않는다. `RunAsync`는 이 불변식을 지킨다(조회 실패 시 곧바로 예외로 빠져나가 `finally`에서 폐기).
- **범위:** 테스트 헬퍼 `RawCounterClient.QueryAsync`에도 동일 CAS 전제가 있으나, 조정이 "해당 코드"(단수)·리뷰어가 `CounterScenario.cs`만 인용했으므로 **한 곳만** 명문화한다(어드바이저 확인).

---

## 범위 이탈
없음. 모든 변경은 테스트/주석/dead-code 삭제 범위 내이며 계획(13_final_plan.md)의 파일 목록을 벗어나지 않는다.
(R-C3의 `using` 유지는 리뷰어 제안의 컴파일 오류를 피한 것으로, 계획/제품 동작에 영향 없음.)

## 미해결 승계
- **Phase 3 교차 검증 부재** — 본 수정이 대응한 지적들은 Claude 단독 리뷰 기반. Codex 재개 후 재리뷰 시 재평가될 수 있다.
- **test 7 `stopTask` 게시 레이스 — 이번 라운드에서 의도적으로 미수정(by design), 오케스트레이터 판단 필요.** 리뷰어 R-C6가 실제 인용한 결함(라인 408/420/468)은 test 7의 이론적 이중 `Stop()` 가능성이나, 조정이 R-C6를 R-C1 테스트 개편 범위로 스코프했고 이번 지시는 "유효 판정된 지적만 수정"이므로 test 7은 건드리지 않았다. 이 항목은 **닫힌 것이 아니라 스코프 밖으로 남겨 둔 것**이며, test 7의 레이스를 닫으려면 오케스트레이터의 별도 지시(또는 [범위 이탈] 승인)가 필요하다. 제안 수정안은 위 R-C6 절 참고.
