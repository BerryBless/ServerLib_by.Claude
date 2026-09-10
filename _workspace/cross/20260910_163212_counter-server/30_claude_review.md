# 30_claude_review — 경합 카운터 서버 독립 리뷰 (Claude 측, mode=review, 라운드 1)

- **리뷰어:** cross-reviewer (Claude) — 구현자와 분리된 독립 시각
- **독립성 준수:** `30_codex_review.md`를 비롯한 **Codex 리뷰 산출물을 일절 읽지 않았다.** 본 리뷰의 모든 지적은 확정 계획·구현 노트·실제 코드·직접 실행 결과에서만 도출했다.
- **입력:** `00_context.md`, `13_final_plan.md`(베이스 `10_codex_plan.md`), `14_claude_final_check.md` §3 확정 해석, `20_impl_notes.md`, `20_test_results.txt`, `30_diff.patch`, `30_new_files.txt`, `30_review_base.txt`
- **리뷰 대상 diff:** **`b005dab` → `5c949ae`** (`30_review_base.txt` 기준). 17파일 +2,477행, 삭제 0.
- **직접 실행한 검증:** `dotnet build ClaudeCodeStudy.sln -c Release`, `dotnet test ClaudeCodeStudy.sln -c Release --no-build`(14+26=40 통과), `CounterExample.Tests` **5회 반복 실행**(26/26 × 5, 실패 0), 그리고 아래 [R-C1]·[R-C4]를 실증하기 위한 스크래치패드 프로브 2건(프로젝트 코드 미수정).

---

## 0. 판정 요약

| 심각도 | 건수 |
|---|---|
| **High** | **0** |
| Medium | 0 |
| Low | 7 |
| [취향] | 5 |
| 하네스 입력 사실 정정 | 3 |

**정확성·보안·회귀 결함은 발견하지 못했다.** 핵심 설계(세션별 순차 디스패치에 기댄 완료 확인 배리어, `Interlocked` 원자 갱신, 배리어 이후 정지 상태에서만 쌍 단언)는 라이브러리 코드로 역추적해 전건 성립을 확인했다. Low 7건은 **테스트 커버리지의 공백**과 **계획 문구 대비 정밀도 부족**에 집중되며, 어느 것도 현재 코드의 오동작을 뜻하지 않는다.

---

## 1. 먼저 확인한 것 — 근거를 붙인 긍정 검증

리뷰의 신뢰도를 위해, 지적이 아닌 **확인 결과**를 근거와 함께 남긴다.

| 검증 항목 | 근거 | 결과 |
|---|---|---|
| **배리어의 성립 근거** — "내 조회 응답 = 내 앞선 증감 전부 적용" | `SocketPipelineSession.cs:190-216` 수신 루프가 `while (TryReadPacket(...)) { await DispatchPacketAsync(...) }`로 **같은 세션의 패킷을 순차 await**한다. `CounterHandler.cs:104-114`의 증감 경로는 `State.Increment()`를 **동기 완료** 후 `ValueTask.CompletedTask` 반환 → 조회가 디스패치되는 시점에 앞선 증감은 이미 두 필드 모두 반영 완료 | **성립** |
| **최종값의 결정성** | 전 연결이 확인 응답을 받은 뒤에만 `connections[0].QueryAsync` 1회(`CounterScenario.cs:231-237`). 그 시점 서버에 미처리 증감 없음 → `Value`·`AppliedOps` 쌍 단언 유효 | **성립** |
| **패킷 ID 충돌** | 리포 전수 스캔: 기존 1~17 + 0xFFFE/0xFFFF. 신규 18·19 충돌 없음. 테스트용 250도 미사용 | **충돌 없음** |
| **미지 ID 250이 앱 경로에 도달하는가** | `SocketPipelineSession.cs:273` `HeartbeatProtocol.IsPing(packetId)`만 가로챔 → 250은 `CounterHandler` 도달 | **정확**(수정 7의 근거 문구도 정확) |
| **클라 수신부가 PONG에 오염되지 않는가** | `SocketPipelineClient.cs:196` PONG 선가로채기 + `PingInterval` 미설정(기본 null, `:110`) → PING 루프 미가동 | **오염 없음** |
| **`SocketException`=상대 종료 단정 금지(수정 5)** | `SocketPipelineSession.cs:375-376` 송신 시한 만료를 `SocketException(TimedOut)`으로 변환 — 주석이 이 근거를 정확히 인용 | **정확** |
| **조회 응답 할당 특성(수정 4)** | `PacketSendExtensions.cs:78-88` `CompleteAsync`가 `IsCompletedSuccessfully` 분기 → 동기 시 무할당, 비동기 시 `AwaitAndReturnAsync` 상태머신 1개. 주석 서술과 일치 | **정확** |
| **응답 16B(수정 1 / F-2)** | `CounterValuePacket.BodySize=16`, `CounterPacketTests.cs:26,60,79-82`가 본문 16과 프레임 20을 **분리 단언** → off-by-4 회피 | **반영** |
| **F-1 public 확정** | 세 타입 전부 `public`, `InternalsVisibleTo` 어느 csproj에도 없음, `CounterExample.Tests.csproj`가 3개 평범한 `ProjectReference` | **반영**(빌드 성공으로 CS0104 함정 회피도 실증) |
| **sln 구성 매핑(수정 9)** | 신규 3개 프로젝트 × 12행 = 36행, 기존 프로젝트와 동일 형태 | **정확** |
| **루프백 전용 바인딩** | `CounterServer/Program.cs:114`, `CounterEndToEndTests.cs:110,430,509` 전부 `Start(port, IPAddress.Loopback)` | **준수** |
| **문서 갱신(수정 3 / F-5)** | `CLAUDE.md`·`AGENTS.md` 양쪽에 예제 2행 + plan 표 1행. `plan/contention_counter_0910.md`에 CLAUDE.md 요구 7개 항목 전부 존재(§1~§6 + §8) | **반영** |
| **F-6 상태 불변의 범위** | `CounterEndToEndTests.cs:315-316, 363-364`가 `Value`·`AppliedOps` **둘 다** 0 단언 | **반영** |
| **회귀** | 기존 `EchoExample.Tests` 14/14 유지. ServerLib 소스는 **신규 패킷 2개 추가 외 변경 0**(diff 확인) → 기존 동작 회귀 표면 없음 | **없음** |
| **[범위 이탈]** | `b005dab` 대비 diff 파일 목록이 계획의 변경 파일 목록과 **정확히 일치**. 계획 밖 파일·설계 변경 없음 | **0건** |
| **테스트 안정성** | `CounterExample.Tests` 5회 연속 실행 26/26 통과, 실패 0 (impl notes §3.2·§3.4의 flaky 제거 주장 뒷받침) | **재현 확인** |

---

## 2. 지적

### [R-C1] Low | `CounterExample.Tests/CounterEndToEndTests.cs:486-542` (`Scenario_UnresponsiveServer_TimesOutAndCleansUp`)
**발생 조건:** 항상(이 테스트의 구조 자체).
**영향:** 계획이 요구한 *"무응답 타임아웃(**실제 작업 취소**·정리 포함)"*(`13_final_plan.md` §테스트 전략, 베이스 §5)에서 **"실제 작업 취소" 절반이 검증되지 않는다.** `20_impl_notes.md` §3.4-(2)는 이 테스트를 "정리까지 검증하도록 강화했다"고 보고하지만, 강화된 것은 **연결 해제 관측**이고 **워커 취소 경로는 애초에 실행되지 않는다.**

**근거(실증):** `CounterScenario.RunAsync`의 순서는 ① 연결 수립 → ② **기준값 조회** `connections[0].QueryAsync(ct)`(`CounterScenario.cs:212`) → ③ 워커 생성(`:221-223`) → ④ `WhenAll` 배리어 → ⑤ 최종 조회다. 응답하지 않는 서버에서는 **②가 먼저 막혀** 1초 기한이 발화하므로 `workers` 배열은 **채워지지도 않는다.**

스크래치패드 프로브(프로젝트 코드 미수정, ServerLib 리스너로 수신 패킷 ID 집계)에서 테스트 8과 동일 조건(2연결, +1/−1, 기한 1초)을 재현한 결과:

```
TimeoutException at 1.01s
서버 수신 집계: Query(18)=1  Increment(3)=0  Decrement(4)=0  기타=0
```

증감 패킷이 **0개** 도달했다 — 워커가 출발조차 하지 않았다는 직접 증거다. 남은 테스트 7(`Scenario_ServerStopsMidRun`)은 배리어에서 멈추지만 **기한이 아니라 연결 해제**로 종료되므로(테스트 자체가 그 fail-fast를 단언), **"워커가 in-flight인 상태에서 기한이 발화하고 정리된다"는 경로는 26개 테스트 어디에도 없다.**

**수정 방향:** 무응답 서버를 "기준값 조회(1회차)는 정상 응답하고 **그 이후 조회만 드롭**"으로 바꾸면 워커가 증감을 모두 보낸 뒤 배리어 조회에서 멈춘 상태로 기한이 발화한다. 그 상태에서 `TimeoutException` + 전 연결 해제 관측을 단언하면 계획 문구가 실제로 검증된다. (테스트 7의 조건 반전과 동일한 기법이라 구현 부담이 작다.)

---

### [R-C2] Low | `CounterServer/CounterHandler.cs:98-100`, `:116-118`
**발생 조건:** 트랜스포트를 경유한 어떤 입력으로도 `:98`의 분기에 진입할 수 없다.
**영향:** 프레임 길이 교차 검증 분기가 **영구 미커버 코드**로 남고, `RequireBodySize`의 **조회(Id=18) 케이스도 테스트되지 않는다.**

**근거:**
```csharp
// CounterHandler.cs:98-100
if (frame.Length != PacketPool.HeaderSize + bodyLength)
    throw new InvalidDataException(...);
```
`SocketPipelineSession.TryReadPacket`(`:249-263`)은 **같은 헤더에서 읽은** `bodyLength`로 `totalLength = HeaderSize + bodyLength`를 계산해 `buffer.Slice(0, totalLength)`를 넘긴다. 따라서 콜백에 도달하는 프레임은 **항상** 그 등식을 만족한다. 테스트 파일 스스로 이를 인정한다 — `CounterEndToEndTests.cs:553-556`: *"여기서 불일치를 만들면 핸들러에 도달하기 전에 프레이밍 단계에서 갈립니다."*
현재 실패 경로 테스트는 `Id=3 / 본문 4B`(`:307`)와 `Id=250 / 0B`(`:356`) 둘뿐이라, `RequireBodySize`의 **`case CounterQueryPacket.Id`(`:117`)** 분기는 한 번도 실행되지 않는다.

**수정 방향:** 소켓을 경유하지 않고 `CounterHandler.HandleAsync(fakeSession, buffer)`를 **직접 호출**하는 단위 테스트를 1개 추가하면 두 공백이 동시에 닫힌다 — (a) `frame.Length`와 헤더 선언이 어긋난 손수 만든 버퍼, (b) `Id=18 + 본문 4B`. 조회가 아니면 `ISession`을 쓰지 않으므로 최소 스텁이면 충분하다.

---

### [R-C3] Low | `CounterServer/CounterHandler.cs:57-59`, `:82-84`
**발생 조건:** 항상(정적 사실).
**영향:** 사문화된 필드가 남고, 클래스 문서가 **코드가 하지 않는 일**을 설명한다.

**근거:** `private static readonly BinaryPacketSerializer Serializer = new();`(`:59`)는 파일 전체에서 **한 번도 사용되지 않는다**(grep 결과 선언 1행 + 주석 2행뿐). 이 핸들러가 처리하는 세 ID(3·4·18)의 본문이 전부 0B라 역직렬화가 필요 없기 때문이다. 그런데 `:82-84`의 `<remarks>`는 *"따라서 `Deserialize`에 넘기기 전에 ID와 길이를 직접 확인해야"* 라고 서술한다 — 이 핸들러는 `Deserialize`를 **호출하지 않는다.** (검증을 직접 해야 한다는 결론 자체는 여전히 타당하지만, 근거 문장이 현재 코드와 어긋난다.) 컴파일 경고는 발생하지 않아 조용히 남는다.

**수정 방향:** 필드와 `using ServerLib.Core.Serialization;`을 삭제하고, `<remarks>` 문장을 *"본문 0B 전용이라 역직렬화 자체를 하지 않으며, 길이·ID 검증만으로 프레임을 판별한다 — `Deserialize<T>`가 타입 ID를 검사하지 않으므로 본문 있는 패킷을 추가할 때도 이 검증이 선행되어야 한다"* 로 교정.

---

### [R-C4] Low | `CounterClient/CounterScenario.cs:191-196`, `:442-445` (전체 기한 단일화)
**발생 조건:** 상대가 수신 버퍼를 비우지 않아 송신이 커널 흐름 제어에 막히는 경우.
**영향:** 베이스 §5의 *"무응답: **연결·송신·조회와 전체 실행에** 유한한 타임아웃을 둔다"* 중 구현된 것은 **전체 실행 기한 1개**뿐이다(`deadlineCts.CancelAfter(options.Timeout)`). ServerLib이 정확히 이 실패 원인을 위해 제공하는 `IClientConnection.SendTimeout`(`IClientConnection.cs:54`, 문서: *"응답 불능 서버로의 송신이 소켓 버퍼 포화 상태에서 영구 블로킹되는 것을 방지"*)은 **설정하지 않는다.**

**재현 미확인 — 반증 쪽 실측을 첨부한다.** 이 지적을 결함으로 승격하려면 "in-flight 송신이 막히고 취소가 무시된다"가 성립해야 하는데, 스크래치패드에서 **읽지 않는 루프백 피어**에게 단일 `Socket.SendAsync`로 512MB를 보낸 결과:

```
완료 sent=536870912 elapsed=0.13s
```

Windows 루프백에서는 **블로킹 전제 자체가 성립하지 않았다.** 더구나 [R-C1]에서 보였듯 `RunAsync`는 기준값 조회에서 먼저 기한에 걸리므로 기본 설정으로는 대량 송신 구간에 도달하지도 않는다. 따라서 **현재 코드의 결함이 아니라 계획 문구 대비 정밀도 부족**으로만 기록한다.

**수정 방향:** (실효보다 계약 일치가 목적이라면) `CounterConnection.ConnectAsync`에서 `_connection.SendTimeout`을 `options.Timeout`의 일부 값으로 설정하고, 클래스 `<remarks>`에 "송신·조회·전체를 각각 어떤 시한이 덮는가"를 한 줄로 명시. 하지 않기로 한다면 계획 문구를 "전체 기한 하나로 수렴시킨다"로 교정해 문서와 코드를 일치시킬 것.

---

### [R-C5] Low | `CounterClient/CounterScenario.cs:221-231`
**발생 조건:** 워커 1개가 실패(연결 끊김·프로토콜 위반)했는데 **나머지 워커는 정상 진행 중**일 때.
**영향:** 베이스 §5는 *"연결·송신 실패 또는 중도 종료: 해당 조회 대기를 실패시키고 **전체 시나리오를 취소한다**"* 를 요구한다. 구현은 앞 절반(`OnDisconnected`에서 대기자 실패, `:425-430`)만 수행하고 **`deadlineCts`를 취소하지 않는다.** `Task.WhenAll(workers)`는 모든 워커가 끝날 때까지 기다리므로, 살아 있는 워커가 배리어에서 멈추면 최초 실패 원인(`IOException`)이 아니라 **기한 만료의 `TimeoutException`으로 퇴화**해 진단성이 떨어진다.

**근거:** `:231` `await ThrowIfDeadlineAsync(Task.WhenAll(workers), ...)` — 개별 워커 실패를 관측해 `deadlineCts.Cancel()`을 호출하는 경로가 없다.

**재현 미확인.** 실제로 이 상황을 만들려면 "한 연결만 죽고 다른 연결은 응답이 끊긴 채 살아 있는" 서버가 필요하다. 테스트 7처럼 서버가 통째로 내려가면 **모든** 대기자가 즉시 실패해 `WhenAll`이 빠르게 완료되므로(실측 fail-fast 10초 예산 내 통과) 현재 테스트로는 재현되지 않는다.

**수정 방향:** 각 워커를 `catch { deadlineCts.Cancel(); throw; }`로 감싸거나 `Task.WhenAny` 기반 first-failure 취소를 두면 원인 예외가 그대로 표면화된다. 채택하지 않는다면 베이스 §5 문구를 "실패는 전체를 취소하지 않고 기한이 수렴시킨다"로 교정할 것.

---

### [R-C6] Low | `CounterExample.Tests/CounterEndToEndTests.cs:408`, `:420`, `:468`
**발생 조건:** `finally`의 `stopTask` 읽기가 IO 스레드 콜백의 쓰기를 관찰하지 못하는 경우.
**영향:** `listener.Stop()`이 **두 스레드에서 동시에** 호출될 수 있고, `SocketPipelineListener.Stop()`은 **멱등·스레드 안전이 아니다** — `_cts?.Cancel()`(`:185`) 실행 중 다른 스레드가 `_cts?.Dispose(); _cts = null;`(`:199-200`)을 끝내면 `ObjectDisposedException`이 나 **테스트의 실제 단언 결과를 가린다.** 이는 구현자가 §3.4-(1)에서 제거하려 했던 결함과 동일한 종류다.

**근거:**
```csharp
Task? stopTask = null;                       // :408  캡처된 지역(비동기화)
    if (n == 2) stopTask = Task.Run(listener.Stop);   // :420  IO 스레드에서 쓰기
...
    if (stopTask is not null) await stopTask;  else listener.Stop();  // :468 테스트 스레드에서 읽기
```

**재현 미확인.** 쓰기와 읽기 사이에 소켓 IO·`Interlocked`·스레드 풀 큐잉이 끼어 x64에서는 사실상 순서가 보장되며, 5회 반복 실행에서 발현하지 않았다. **이론적 지적이다.**

**수정 방향:** `stopTask`를 `TaskCompletionSource`나 `Interlocked.Exchange`로 게시하거나, 단순히 `finally`에서 `stopTask`를 무조건 `await`하고 `Stop()`을 콜백에서만 부르는 형태로 단일화.

---

### [R-C7] Low | `CounterClient/CounterScenario.cs:397-401`, `:456-479`
**발생 조건:** 조회가 **in-flight 상태로 포기**된 뒤(취소·송신 실패로 `catch`에서 대기자를 회수, `:476`) **같은 연결에서 다시 조회**할 때. 그 사이 도착한 이전 요청의 응답이 새 대기자를 완료시킨다.
**영향:** 새 조회가 **낡은 스냅샷**을 받는다. 최종 검증에 쓰이면 오판정이 될 수 있다.

**근거:** 응답 매칭이 요청 ID 없이 "연결당 미완료 조회 1건"이라는 전제에만 의존한다(`CounterQueryPacket.cs:45-46`, `CounterScenario.cs:362`). `OnReceived`(`:399`)는 도착한 아무 응답이나 현재 대기자에게 귀속시키며, 주석은 *"대기자가 없는 응답(중복·지연 도착)은 조용히 버린다"*(`:401`)로 **반대 방향만** 방어한다.

**재현 미확인 — 현재 흐름에서는 도달 불가.** 조회를 포기하는 경로는 취소·송신 실패뿐이고, 두 경우 모두 `RunAsync`가 곧바로 예외로 빠져나가 `finally`에서 연결을 `DisposeAsync`한다(`:257-267`). 재조회가 일어나지 않는다.

**수정 방향:** 코드 변경보다 **전제의 명문화**가 적절하다. `QueryAsync`의 `<remarks>`에 *"포기된 조회가 있는 연결은 재사용하지 않는다(요청 ID가 없어 지연 응답과 새 요청을 구분할 수 없다) — 실패한 연결은 반드시 Dispose한다"* 를 불변식으로 못 박을 것.

---

## 3. [취향] — 심각도 없음

- **[취향-1] `ThrowIfDeadlineAsync`(`CounterScenario.cs:346-359`)는 완전 중복이다.** `RunAsync`의 바깥 `catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)`(`:251-256`)가 **같은 조건·같은 메시지**로 `TimeoutException`을 던진다. 헬퍼를 제거해도 동작이 동일하며, 동일 문자열이 두 곳에 있어 문구 수정 시 갈라질 수 있다.
- **[취향-2] `BuildSchedule` 중복.** `CounterScenario.cs:315-333`와 `CounterStateTests.cs:174-192`에 동일 알고리즘이 두 벌 있다. 기대값이 분포에 의존하지 않아 위험은 없다.
- **[취향-3] `CounterEndToEndTests.cs:234-235`의 `Assert.True(result.Elapsed < 30초)`는 사실상 항진 명제다.** 30초를 넘겼다면 그 전에 `TimeoutException`이 났을 것이다. 실측 여유를 남기려면 훨씬 작은 상한(예: 10초)이 의미 있다.
- **[취향-4] `InitialSnapshot`을 기대값에서 차감하지 않는다**(`CounterScenario.cs:242`). 같은 서버에 두 번째 실행 시 FAIL(종료 코드 1)이 나는데, 원인은 "경합 처리 결함"이 아니라 "서버 재시작 필요"다. `Program.cs:60-65`가 경고를 먼저 출력해 오해는 완화되지만, `fin.Value - initial.Value`로 판정하면 경고 자체가 불필요해진다. (계획이 "새 서버 단일 실행"을 전제로 못 박았으므로 현 구현도 계획 준수다.)
- **[취향-5] 미관측 faulted `TaskCompletionSource`.** `CounterConnection.DisposeAsync`(`:485-486`)·`RawCounterClient.DisposeAsync`(`:638`)가 아무도 await하지 않을 대기자에 `TrySetException`을 건다. .NET Core에서 미관측 Task 예외는 기본적으로 무시되므로 동작 영향은 없다.

---

## 4. 하네스 입력 사실 정정 (코드 결함 아님 — 후속 리뷰어를 위한 기록)

1. **기준 커밋이 파이프라인 도중 이동했다.** `00_context.md:13`이 적은 `3aa7a80`은 **컨텍스트 작성 시점에는 정확한** 기준이었고, 그 이후 `8b8e07e`·`b005dab`(둘 다 cross-verify 하네스 설정 변경)이 추가되면서 실제 리뷰 기준이 `30_review_base.txt`의 **`b005dab`** 로 이동했다. **`3aa7a80` 대비로 diff를 뜨면 `CLAUDE.md`의 하네스 트리거 변경이 섞여 들어와 "계획 밖 변경"으로 오독된다** — 나도 첫 시도에서 이 함정에 걸렸다가 `30_review_base.txt`로 교정했다. 후속 라운드는 반드시 `b005dab..HEAD`를 볼 것.
2. **`20_impl_notes.md:6`의 "커밋: 하지 않음"은 현재 리포 상태와 다르다.** 구현 전량이 **`5c949ae` "추가: 경합 시연용 공유 카운터 서버 예제 (교차 검증 파이프라인 산출)"** 로 이미 커밋되어 있고 워킹트리는 깨끗하다(Stop 훅 자동 커밋으로 보인다). 미커밋 변경을 찾으려 하면 헛수고가 된다.
3. **`20_impl_notes.md:5`의 "경고 0"은 요약 과잉이다.** 클린 `dotnet build ClaudeCodeStudy.sln -c Release`는 **CS0419 경고 10건**을 낸다. 다만 전부 `ServerLib/Interface/IServerListener.cs`의 **기존** 경고이고 해당 파일은 diff에 없으므로 **회귀가 아니다.** `20_test_results.txt:33`에는 이 사실이 정확히 공개되어 있으므로 보고 체계의 결함은 아니고 요약 문장만 부정확하다.

**추가 기록(계획이 보고 의무로 넘긴 항목의 이행 확인):** F-3(배리어 필요성 미증명 한계) → `CounterEndToEndTests.cs:31-34`와 설계 문서 §7에 기록됨. F-4(워킹트리 서술 교정) → 설계 문서 부록에 기록됨. F-7/D2(unsafe 토글 후속 후보) → 설계 문서 §8 첫 항목으로 기록됨. **세 건 모두 이행 확인.**

---

## 5. 검증 축별 결론

| 축 | 결론 |
|---|---|
| **버그(정확성)** | 결함 없음. 배리어·원자 갱신·쌍 단언 시점 전부 라이브러리 코드로 역추적해 성립 확인 |
| **회귀 가능성** | 없음. ServerLib 변경은 신규 패킷 2개 **추가**뿐이고 기존 14개 테스트 유지 |
| **보안** | 문제 없음. 서버·테스트 전부 루프백 전용 바인딩, 손상·미지 프레임은 상태 변경 **전에** 거부되고 해당 세션만 종료. 인증 부재·연결 상한 미설정은 계획의 비범위이며 `Program.cs:110-113`에 실서비스 권고가 명시됨 |
| **성능(핫패스)** | 문제 없음. 증감 경로 `ValueTask.CompletedTask` 무할당, 증감·조회 프레임 정적 1회 직렬화 후 재사용, `struct` 패킷으로 박싱 없음, 전통 락 0개. 실측 14,000 왕복 ≈ 0.12초 |
| **요구사항 충족** | 수정 1~10 + F-1·F-2 확정 해석 + F-3·F-5·F-6 전건 반영. 미달은 [R-C4](송신·조회 개별 시한)·[R-C5](실패 시 전체 취소)의 **문구 수준 정밀도**뿐 |
| **테스트 누락** | [R-C1](워커 취소 경로 미검증 — 가장 실질적)·[R-C2](핸들러 직접 호출 단위 테스트 부재). 그 외 `CounterScenarioOptions.Validate()`와 "미완료 조회 1건" `InvalidOperationException` 경로도 미검증(경미) |
| **범위 이탈** | **0건**(정확한 base `b005dab` 대비 확인) |

---

## 종합

**High 0건.** 이 diff에는 정확성·보안·회귀 결함이 없다. 배리어 설계가 `SocketPipelineSession.cs:190-216`의 세션별 순차 await에 정확히 기대어 성립하고, 패킷 ID 18·19는 충돌이 없으며, sln 구성 매핑 12행×3, 기존 테스트 14/14 유지, diff가 계획 파일 목록과 정확히 일치(범위 이탈 0)함을 각각 근거와 함께 확인했다. 신규 26개 테스트는 5회 반복 실행에서 전량 통과했다(flaky 미관측). **없는 High를 만들어 리뷰를 무겁게 보이게 하지 않는다 — 2026-09-10 Pipe 데드락 오탐이 정확히 그 실패 양식이었다.**

**병합 가능 의견: 병합 가능.** Low 7건은 어느 것도 병합을 막지 않는다. 다만 **[R-C1](무응답 테스트가 워커 취소 경로를 전혀 실행하지 않음 — 서버 수신 집계 `Increment=0`으로 실증)** 은 *계획이 요구했고 구현 노트가 이행했다고 보고한 검증이 실제로는 존재하지 않는* 유형이라, 병합 전이든 후속 라운드든 **우선 처리 권고**한다. 함께 처리하면 비용이 거의 없는 것은 **[R-C2]**(핸들러 직접 호출 테스트 1개로 미도달 분기 + Id=18 본문 검증 동시 해소)와 **[R-C3]**(미사용 필드 삭제 + 문서 1문장 교정)이다. [R-C4]·[R-C5]는 **코드 수정 대신 계획·주석 문구를 코드에 맞추는 쪽**을 권한다(둘 다 재현 미확인이며 [R-C4]는 반증 실측까지 확보했다). [R-C6]·[R-C7]은 이론적 지적으로, 불변식을 주석에 명문화하는 선에서 닫아도 무방하다.

**독립성 선언:** 본 리뷰 작성 시점까지 `30_codex_review.md` 등 Codex 리뷰 산출물을 **읽지 않았다.**
