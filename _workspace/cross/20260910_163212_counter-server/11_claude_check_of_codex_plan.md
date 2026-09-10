# 11_claude_check_of_codex_plan — Codex 계획 검토 (Claude 측)

- **검토자:** cross-planner (Claude), mode=check
- **입력:** `00_context.md`, `10_codex_plan.md` (참조: `10_claude_plan.md`)
- **검토 방법:** Codex가 인용한 **모든 파일:라인을 원본 소스에서 직접 재확인**했다. 코드 근거 없는 지적은 `[취향]`으로 분리했다.
- **판정 요약:** **조건부 APPROVE 성향.** 설계의 핵심(패킷 재사용·Interlocked·세션별 순차 디스패치 기반 배리어·Id 18/19·신규 테스트 프로젝트)은 **내 독립안과 무관하게 수렴**했고 코드 근거도 정확하다. Blocking High 0건. Med 3건(테스트 seam 자기모순, 문서 갱신 누락, 테스트 규모 미명시)만 통합 계획에서 해소하면 된다.

| 심각도 | 건수 | 항목 |
|---|---|---|
| High | **0** | — |
| Med | 3 | P-C1, P-C2, P-C3 |
| Low | 4 | P-C4, P-C5, P-C6, P-C7 |
| [취향] | 2 | 취향-1, 취향-2 |

---

## 축 2. 잘못된 가정 — 코드로 반증 : **반증 0건**

Codex가 제시한 코드 인용을 전수 재확인했다. 아래는 대필이 아닌 **내가 직접 원본을 열어 확인한 결과**다.

| Codex 인용 | 주장 | 재확인 결과 |
|---|---|---|
| `IncrementPacket.cs:5` / `DecrementPacket.cs:5` | 본문 없는 ±1 명령 struct, Id=3·4 | **정확.** `IncrementPacket.cs:5-11` = `struct`, `Id=3`, `GetBodySize()=>0`, 빈 Serialize/Deserialize. |
| `PacketSendExtensions.cs:33` | 컨텍스트 문서의 `SessionContextExtensions.SendAsync<T>`는 오기이며 실제는 `PacketSendExtensions.SendAsync<T>` | **정확 — 그리고 `00_context.md:18`의 오류를 잡아낸 유효한 교정.** 33행이 정확히 `public static ValueTask SendAsync<T>(this ISession session, ...)`. |
| `PacketSendExtensions.cs:88` | 비동기 실패 시에도 `finally`에서 풀 버퍼 반납 | **정확.** 88-92행 `AwaitAndReturnAsync`가 `try/finally { ArrayPool<byte>.Shared.Return(buffer); }`. |
| `BinaryPacketSerializer.cs:59` | `Deserialize<T>`가 헤더를 건너뛰고 본문만 읽으며 타입 ID 일치·본문 전체 소비를 **검사하지 않는다** | **정확.** 59-71행. `source.Slice(HeaderSize)` 후 무조건 `new T()`. → 헤더 id 기준 라우팅이 필수라는 Codex의 결론도 옳다. |
| `SocketPipelineSession.cs:190` | 세션 수신 루프가 패킷별 콜백을 순차 `await` | **정확.** 190행이 `while (TryReadPacket(...))` 프레이밍 루프이고 **197행이 `await DispatchPacketAsync(packet, packetId);`** 다. 배리어 프로토콜의 전제 성립. |
| `SocketPipelineSession.cs:199` | 핸들러 예외는 기존 세션 오류 처리로 전달되고 다른 세션은 계속 동작 | **정확.** 198-215행: `catch` → `OnReceiveError` 통지 → `return` → `finally`에서 해당 세션만 정리. |
| `PacketPool.cs:45` | 기존 4바이트 헤더 `[PacketId(2)\|BodyLength(2)]` | **정확.** 45-46행 주석 + `HeaderSize = 4`. |
| `EchoEndToEndTests.cs:39` / `:51` | 임시 포트·유한 대기 패턴 | **정확.** 39행 `EchoTimeoutMs = 5_000`, 51행 `private static int GetFreePort()`. |
| `AGENTS.md:46` / `:93` / `:124` | 플랜 문서화 규칙 / XML 주석 규칙 / 인라인 주석 규칙 | **정확.** 46=「플랜 문서화 규칙」, 93=「인터페이스 및 API 문서화(주석) 규칙」 본문, 124=「네트워크·메모리 관련 선언부 인라인 주석 규칙」. |
| Id 18·19 미사용 | 신규 Id 충돌 없음 | **정확.** `grep "const ushort Id" ServerLib/` 전수 = 1~17 + `0xFFFE`/`0xFFFF`. 18·19 미사용 확인. |

**이 축에서 만들어낼 지적이 없다.** Codex 계획의 사실 기반은 이 검토에서 확인한 범위 내에서 결함이 없다. 유일한 사실 오류는 코드가 아닌 리포 상태에 관한 것으로 아래 P-C5로 분리했다.

---

## 축 1. 요구사항 누락

### [P-C2] Med | 프로젝트 문서 갱신 2건이 변경 파일 목록에서 누락

**근거.**
- `CLAUDE.md`(=`AGENTS.md`) 「프로젝트 개요」의 **"예제 코드 위치"** 목록은 모든 예제 `Program.cs`를 열거하며, 같은 절이 *"새 기능을 추가할 때 Program.cs의 예제도 함께 업데이트할 것"* 을 명시한다. `CounterServer/Program.cs`·`CounterClient/Program.cs` 2개 예제가 신설되는데 Codex의 §2 변경 파일 목록에 두 문서 어느 쪽도 없다.
- `AGENTS.md:46`「플랜 문서화 규칙」이 요구하는 **"현재 플랜 문서 목록" 표에 행 추가**도 없다. Codex는 `plan/contention_counter_0910.md` 신규 작성만 적었다.
- **파일이 2개다.** `diff CLAUDE.md AGENTS.md` 결과 두 문서는 커밋 훅 절과 말미 하네스 절만 다른 **미러**이며, 「예제 코드 위치」 목록과 「플랜 문서 목록」 표는 **양쪽 모두에 존재**한다. 한쪽만 고치면 문서가 갈라진다.

**제안.** 변경 파일 목록에 `CLAUDE.md`·`AGENTS.md` 2건을 추가하고, 각각 (a) 예제 목록에 2줄, (b) 플랜 문서 목록 표에 1행을 넣는다. 구현 순서 6단계("주석·설계 문서 작성")에 이 항목을 명시할 것.

> **자기 공개 (대칭 결함).** 내 `10_claude_plan.md` §4-5는 `CLAUDE.md`만 적고 `AGENTS.md`를 빠뜨렸다. **이 지적은 내 계획에도 동일하게 적용된다.** 통합 계획에서 양쪽 다 반영해야 한다.

### [P-C7] Low | 최종값 불일치 시 원인 분해 수단이 없다

**근거.** `CounterValuePacket` 본문이 `long Value` 8바이트뿐이라, 최종 검증이 FAIL일 때 **"패킷이 도달하지 않았다"** 와 **"도달했으나 갱신이 유실됐다"** 를 구분할 관측량이 없다. Codex 계획은 `Interlocked` 전용이므로 후자가 발생하지 않는다는 전제 위에서 이 단순화가 성립하지만, 그 전제 자체를 계기로 확인할 방법이 없다는 뜻이기도 하다.

**제안.** 본문을 16B(`Value` + `AppliedOps`)로 확장하고 `AppliedOps`는 **변경 op(Id 3·4)만** 계수한다(조회·드롭 패킷 제외). 채택 시 **`Value`와 `AppliedOps`는 각각 원자적이나 쌍으로 읽는 것은 원자적이지 않으므로 정지 상태 조회에서만 의미를 갖는다**는 제약을 `<remarks>`에 명시해야 한다. — 다만 이는 §종합의 **유일한 와이어 포맷 충돌**이므로 오케스트레이터 결정 사항이다(Low로 둔 이유).

---

## 축 3. 과도한 설계

### [P-C4] Low | `internal` + `InternalsVisibleTo` 이중 장치는 리포 관례와 어긋난다

**근거.** Codex는 `CounterServer.csproj`·`CounterClient.csproj` 양쪽에 *"테스트 어셈블리에만 내부 접근 허용"* 을 넣고 `CounterState`·`CounterHandler`·`CounterScenario`를 전부 `internal`로 둔다. 그런데 현행 테스트 프로젝트는 정반대 입장을 **주석으로 명문화**하고 있다:

```
EchoExample.Tests/EchoExample.Tests.csproj:18
<!-- ServerLib를 소스 참조: public API만 사용 (internal 접근 불필요, InternalsVisibleTo 대상이 아님) -->
```

예제 실행 파일의 타입은 NuGet 배포 대상(`pack.ps1`)이 아니므로 `public`으로 두어도 공개 표면이 넓어지지 않는다. `InternalsVisibleTo`는 프로젝트 2개에 빌드 설정을 추가하고, 어셈블리 이름 변경 시 조용히 깨지는 결합을 만든다.

**제안.** 예제 프로젝트의 재사용 단위(`CounterState`/`CounterHandler`/`CounterScenario`)는 `public`으로 선언하고 `InternalsVisibleTo`를 제거한다. **ServerLib 쪽 캡슐화(Transport `internal`)는 그대로 유지**되므로 프로젝트 규칙 위반이 아니다.

---

## 축 4. 기존 구조와의 충돌

### [P-C6] Low | 솔루션 등록 시 x64/x86 구성 행 누락 함정 미언급

**근거.** Codex §2는 `ClaudeCodeStudy.sln`에 *"신규 프로젝트 3개와 빌드 구성 등록"* 이라고만 적었다. 그런데 이 솔루션의 `GlobalSection(ProjectConfigurationPlatforms)`는 **프로젝트당 12행**(Debug/Release × Any CPU/x64/x86 × ActiveCfg/Build.0)을 갖는다 — `ClaudeCodeStudy.sln:23-34`가 `{B70D156F-...}` 한 프로젝트에 대해 정확히 12행이다. `dotnet sln add`는 `Any CPU` 4행만 기록하는 경우가 있어, x64/x86 구성에서 신규 프로젝트가 조용히 빌드에서 빠진다.

**제안.** 구현 순서 6단계에 "기존 항목의 12행 패턴을 복제해 x64/x86 행 존재를 확인" 체크를 명시. (내 계획 §9-8과 동일 지적 — 이건 내 쪽에 있고 Codex 쪽에 없다.)

### [P-C5] Low | 기준 워킹트리 상태 서술이 사실과 다르다 (이 검토의 유일한 반증)

**근거.** Codex 3행: *"현재 미추적 `.claude/settings.local.json`이 있어 워킹트리가 완전히 깨끗하지는 않다."*

실측:
```
$ git rev-parse HEAD
3aa7a80205ae84f96f6fe780a3cbde255178faf0
$ git status --porcelain
(출력 없음)
$ git check-ignore -v .claude/settings.local.json
"C:\Users\aaa/.config/git/ignore":1:**/.claude/settings.local.json   .claude/settings.local.json
```
파일은 존재하지만 사용자 **전역 gitignore**(`~/.config/git/ignore`)에 의해 무시되므로 미추적(untracked)이 아니라 **무시됨(ignored)** 이다. 무시된 파일은 정의상 워킹트리를 더럽히지 않는다. `00_context.md:13`의 "워킹트리 깨끗"은 **정확하다.**

**제안.** 기준 상태 서술을 정정한다. 영향은 없으나(파일 수정을 하지 않았으므로) 베이스라인 검증 주장의 신뢰도에 관계된다.

---

## 축 5. 예외 처리

Codex의 실패 경로 설계는 **충실하다.** 사전 길이 검증(§5 1항)과 그 근거(`Deserialize<T>`가 타입 ID를 검사하지 않음)는 정확하고, 송신 실패·중도 종료·무응답 타임아웃·부분 초기화 실패·종료 코드까지 축이 빠짐없이 덮여 있다. 특히 다음 두 문장은 내 계획보다 낫다:

- *"단순히 `WaitAsync`만 종료하지 않고 실제 작업 취소와 연결 정리까지 수행한다."* — 내 §7의 `.WaitAsync(TimeSpan)`은 대기만 풀고 배후 작업을 방치한다. **Codex 안을 채택할 것.**
- *"이미 적용된 증감은 롤백하지 않으며 자동 재송신하지 않는다. 재시도하면 중복 적용 여부를 알 수 없기 때문이다."* — at-least-once 재전송이 카운터 검증을 무의미하게 만든다는 판단이 정확하다.

**설계 분기 1건 (결함 아님, 조정 필요).** Codex는 미지 ID·잘못된 본문을 *"해당 연결의 오류로 처리"* 하여 세션을 종료시킨다(라이브러리 기본 동작). 나는 드롭 + 카운터 증가 + 세션 유지를 택했다. 둘 다 `SocketPipelineSession.cs:198-215`의 실제 동작과 정합하며 어느 쪽도 틀리지 않다. 학습용 예제라는 목적에서는 "드롭 + 관측 카운터 + 라이브러리 기본 동작을 주석으로 병기"가 원인 파악에 유리하다고 보지만, **코드 근거로 우열을 가릴 수 없으므로 심각도를 매기지 않는다.** 오케스트레이터가 택일할 것.

---

## 축 6. 테스트 전략

### [P-C1] Med | "완료 확인 절차" 테스트가 계획 내부에서 자기모순이며, 필요한 seam이 어디에도 없다

**근거 — Codex 계획의 두 문장이 직접 충돌한다.**

> §6 테스트: *"**완료 확인 절차:** 한 연결의 마지막 명령을 **테스트용 비동기 신호로 지연시켜**, 송신 완료만으로 검증이 끝나지 않음을 확인한다."*

> §4 동시성: *"증감 경로는 **동기 갱신 후 `ValueTask.CompletedTask`를 반환**한다. 패킷마다 로그나 작업 Task를 만들지 않는다."*

증감 경로를 테스트에서 비동기로 지연시키려면 핸들러에 주입 가능한 대기 지점(seam)이 있어야 하는데, §4는 그 경로가 항상 동기 완료라고 못박았고, **§2 변경 파일 목록에도 `CounterHandler` 설명에 seam이 없다**("헤더 검증, 패킷 분기, 상태 갱신·조회 응답"). 구현자는 이 테스트를 작성하는 순간 계획에 없는 설계 변경을 하게 된다.

**두 번째 문제 — 테스트가 의도한 것을 증명하지 못한다.** Codex의 검증 절차는 연결마다 마지막 명령 뒤에 조회를 붙이는 방식이다. 따라서 한 연결의 마지막 증감을 지연시키면 **같은 연결의 조회 응답도 함께 지연된다**(세션 내 순차 디스패치, `SocketPipelineSession.cs:197`). 배리어가 그 지연을 흡수하므로 최종값은 여전히 맞는다. "송신 완료만으로 검증하면 틀린다"를 보이려면 **배리어 없는 순진한 절차를 실제로 실행해 실패를 관찰**해야 하는데, 그건 결과가 스케줄러 의존이라 flaky 테스트가 된다.

**제안.** 둘 중 하나로 확정한다.
1. **(권장)** seam을 명시적으로 계획에 넣는다 — `CounterHandler`에 `Func<ValueTask>? OnBeforeApply { get; set; }`(프로덕션 `null`, 핫 패스 null 분기 1회). 그리고 테스트의 단언을 **"지연된 연결의 op까지 최종값에 반영된다"**(=배리어가 실제로 기다린다)로 바꾼다. 순진한 절차의 실패를 단언하지 않는다.
2. seam을 도입하지 않고, 이 테스트 항목을 삭제한 뒤 §4의 "동기 완료" 정책을 유지한다.

Med로 둔 이유: 수정 비용이 작고(필드 1개 + null 분기 1개) 대안이 명확하다.

### [P-C3] Med | 테스트 항목이 규모·단언값 없이 서술되어 구현자가 재량으로 채워야 한다

**근거.** §6의 테스트 목록은 대상은 망라했으나 **검증 가능한 수치가 없다.**
- *"여러 작업이 같은 실제 `CounterState`를 갱신한 뒤 N−M 확인"* — 태스크 수·반복 수·기대값 미지정.
- *"여러 연결의 처리 완료 확인 후 최종 조회"* — 연결 수·op 수 미지정. (§1의 8연결 × 1,000/750은 **콘솔 데모 시나리오**의 값이고 테스트 값으로 명시되지 않았다.)
- *"응답의 0·음수·`long` 경계값 왕복"* — 케이스 목록 미지정.
- 타임아웃 값 미지정. `EchoEndToEndTests.cs:39`가 `5_000`ms를 상수로 두는 관례가 이미 있다.

이 상태로는 구현자마다 다른 테스트가 나오고, 리뷰 단계에서 "테스트가 충분한가"를 판정할 기준이 없다.

**제안.** 각 테스트에 **테스트명 + 부하 파라미터 + 정확한 단언식**을 표로 확정한다. 예: `ConcurrentClients_FinalValueIsDeterministic` = 8클라 × (500 add + 300 sub) → `Value == 1600`. 타임아웃은 단일 왕복 5s / 다중 클라 부하 30s 상수로 고정.

### 테스트 전략에서 Codex가 더 나은 점 (통합 시 채택 권장)

- **"조회 중 서버 종료"·"무응답 타임아웃"** 실패 테스트 — 내 E1~E6에는 없다. 서버가 조회 왕복 중 사라졌을 때 클라가 즉시 실패하는지가 이 설계의 실질적 취약점인데 Codex만 덮었다.
- **"코드 재사용: 테스트에 서버·클라이언트 알고리즘을 복제하지 않고 실제 내부 핸들러와 실행기를 호출한다"** — `EchoEndToEndTests.cs:22-24`가 와이어링을 복제하고 있는 현행 부채를 반복하지 않겠다는 판단으로, 내 D7과 독립적으로 같은 결론에 도달했다.
- **"임시 포트 재바인딩 충돌은 시작 단계에서만 제한적으로 재시도"** — `GetFreePort()`의 TOCTOU 창(`EchoEndToEndTests.cs:44-49` 주석이 인정하는 문제)에 대한 실질적 완화. 내 계획엔 없다.

---

## [취향] — 코드 근거로 우열을 가릴 수 없는 항목

### [취향-1] unsafe(비원자) 모드 토글의 유무 = **범위 결정 사항** (오케스트레이터 판단)

Codex는 §8에서 *"의도적으로 잘못된 `counter++` 모드와 비결정적 실패 시연"* 을 명시적 **비범위**로 선언했다. 내 계획(D5)은 `SharedCounter.UseInterlocked` 토글로 이를 포함했다.

**Codex 안이 요구사항을 위반하지 않는다.** `00_context.md:9`는 *"**경합 처리가 올바르면** 결정적 기대값이 나온다는 것이 학습 포인트"* 라고 적고 있고, 이는 올바른 경로만으로 충족된다.

양쪽 리스크를 한 줄씩 적어 판단 재료만 제공한다:
- **Codex 안:** 결과가 항상 기대값과 같으므로 학습자가 "`Interlocked`가 왜 필요한가"를 대비로 체감할 수 없다.
- **내 안:** `Thread.SpinWait`이 IO 스레드의 `OnReceived` 내부에서 돌아 해당 세션 읽기 루프를 직렬 정지시키며(`SocketPipelineSession.cs:197`), `00_context.md:10`의 *"규모: 작게"* 지시에 대한 scope creep 소지가 있다.

**나는 이 항목에 심각도를 매기지 않는다.** 내 기능을 상대 결함으로 계상하지 않기 위해서다.

### [취향-2] 포트 9300(Codex) vs 9010(내 안)

둘 다 리포 전체에서 미사용을 확인했다(`grep -rn "9300\|9010"` → 0건). 기존 사용: 9000(Echo)·8080(EchoWeb)·9100(admin)·9200(AuthServer). **9300이 9100/9200과 100 간격 계열을 이룬다는 점에서 오히려 일관적이다.** 어느 쪽이든 무방.

---

## 검토했으나 결함이 아닌 것 (축 스윕 증빙)

| 검토 항목 | 결론 |
|---|---|
| §4 *"증감 프레임 두 개는 한 번 직렬화한 배열로 보관·재사용"* 이 8개 연결에서 공유될 때의 안전성 | **안전.** 버퍼를 변경하지 않으므로(read-only) 동시 송신에도 손상 불가. `CLAUDE.md` 「Client/Program.cs」 항목이 *"1회 직렬화 후 버퍼 재사용 무할당 패턴"* 을 이미 확립된 관례로 기술하고 있다. |
| 테스트 프로젝트가 콘솔 **Exe** 프로젝트 2개를 `ProjectReference`하는 것 | **동작함.** 참조된 어셈블리의 진입점은 테스트 어셈블리의 진입점과 충돌하지 않는다. (내 계획도 동일 구조이므로 어느 쪽에도 불리하지 않다.) |
| `InternalsVisibleTo` + top-level 문 조합에서 `Program` 타입 모호성(CS0104) | **주의사항일 뿐 결함 아님.** 두 예제 Exe의 암묵 `internal partial class Program`이 테스트에 동시 노출되나, 테스트가 `Program`을 비수식(unqualified) 참조하지 않는 한 발생하지 않는다. P-C4를 채택하면 자연 소멸. |
| 서버가 PONG(`0xFFFF`)을 미지 패킷으로 오인해 정상 세션을 죽일 위험 | **없음.** 하트비트는 클라→PING / 서버→PONG 단방향이다(`SocketPipelineClient.cs:153` `BuildPing`, `SocketPipelineSession.cs:273` PING만 가로챔). 서버는 PONG을 수신하지 않는다. |
| 최종 기대값 산술 `2,000 = 8 × (1,000 − 750)` | **정확.** |

---

## 내 계획(`10_claude_plan.md`)의 자기 수정 — F6은 틀렸다

이 검토 중 Codex와 무관하게 발견했으며, 고치지 않으면 `13_final_plan.md`로 전파된다.

`10_claude_plan.md` §2 **F6**: *"예약 ID `0xFFFE`(PING)/`0xFFFF`(PONG)는 `DispatchPacketAsync`가 가로채 앱 `OnReceived`를 호출하지 않는다"* → **서버 방향에서 부정확하다.**

`SocketPipelineSession.cs:273`은 **`IsPing(packetId)`만** 검사한다. 따라서 서버 기준으로는:
- `0xFFFF`(PONG)를 서버로 보내면 **가로채지 않고 `OnReceived`에 도달한다.**
- `0xFFFE`(PING)라도 본문이 손상돼 `TryBuildPongBuffer`가 `null`을 반환하면 *"기존 동작과 동일하게 아래 일반 경로로 떨어진다"*(`SocketPipelineSession.cs:279` 주석) → 역시 `OnReceived`에 도달한다.

양방향 가로채기는 클라이언트 측(`SocketPipelineClient.cs:196`, PONG만)에서만 성립한다.

**단, E5의 결론은 바뀌지 않는다.** "미지 패킷 드롭" 테스트에 예약 ID 대신 **미할당 Id=250**을 쓰라는 권고는 여전히 옳다 — 하트비트 프로토콜의 내부 분기에 테스트가 결합되는 것을 피하는 편이 견고하기 때문이다. F6의 *근거 문장*만 위와 같이 교정하면 된다.

---

## 종합

### 두 계획의 실질적 차이

**독립 작성된 두 계획이 다음 6개 핵심 결정에서 완전히 일치했다.** 이는 설계 위험이 낮다는 강한 신호다.

| 수렴 항목 | 양측 공통 결론 |
|---|---|
| 증감 명령 | 기존 `IncrementPacket`(3)·`DecrementPacket`(4) 재사용, 수량 필드 추가 안 함 |
| 신규 패킷 Id | 조회 **18**, 값 응답 **19** (충돌 없음, 독립 확인) |
| 조회 요청 본문 | **0바이트** |
| 패킷 위치 | `ServerLib/Core/Serialization/Packets/` |
| 최종값 검증 | 연결별 조회 응답으로 그 세션의 처리 완료를 확인 → **전원 확인 후 최종 1회 조회** (세션 내 순차 디스패치 `SocketPipelineSession.cs:197`에 근거) |
| 테스트 배치 | 신규 `CounterExample.Tests` 프로젝트 (`EchoExample.Tests` 확장 아님) |
| 라우팅 | `Deserialize<T>`가 타입 ID를 검사하지 않으므로(`BinaryPacketSerializer.cs:59`) 헤더 id 기준 분기 + 사전 길이 검증 |

**실질적 차이는 4개뿐이다.**

| # | Codex | Claude | 성격 |
|---|---|---|---|
| ① | 값 응답 본문 **8B** (`Value`) | 값 응답 본문 **16B** (`Value` + `AppliedOps`) | **와이어 포맷 충돌 — 택일 필수** |
| ② | `Interlocked` 전용 | + `UseInterlocked=false` 비원자 모드 토글 | 범위 결정 ([취향-1]) |
| ③ | 손상·미지 패킷 → **세션 종료**(라이브러리 기본) | 드롭 + 관측 카운터 + **세션 유지** | 설계 분기 (축5) |
| ④ | 예제 타입 `internal` + `InternalsVisibleTo` | 예제 타입 `public` | 관례 문제 ([P-C4]) |

### 오케스트레이터가 반드시 결정해야 할 단 하나

**① `CounterValuePacket` 본문을 8B로 할 것인가 16B로 할 것인가.** 나머지는 어느 쪽을 골라도 계획이 성립한다.

- **8B 지지(Codex):** `Interlocked` 전용이면 갱신 유실이 원천 불가하고 TCP는 패킷을 잃지 않으므로, `AppliedOps`는 증명할 것이 없는 필드다. `00_context.md:10`의 "작게" 지시에 더 부합한다.
- **16B 지지(Claude):** ②에서 비원자 모드를 채택하면 `AppliedOps`는 **필수**가 된다 — 그것 없이는 "값이 틀리다"가 갱신 유실인지 패킷 유실인지 구분되지 않아 학습 포인트 자체가 성립하지 않는다. `Interlocked` 전용을 택하더라도 FAIL 시 원인 분해 계기로 쓰인다.
- **결정 규칙(권장):** **②와 연동해 함께 결정하라.** 비원자 모드를 넣으면 16B, 넣지 않으면 8B. 두 항목을 따로 정하면 `AppliedOps`가 아무것도 증명하지 못하는 필드로 남거나(전자 미채택 시), 관측 수단 없는 데모가 된다(후자 미채택 시).
- 16B 채택 시 필수 제약: `AppliedOps`는 **변경 op(Id 3·4)만** 계수하고 조회·드롭 패킷은 제외하며, `Value`/`AppliedOps` **쌍의 읽기는 원자적이지 않으므로 정지 상태 조회에서만 의미를 갖는다**를 `<remarks>`에 명시한다.

### 통합 계획 권장안

**베이스: Codex 계획의 골격 + 아래 병합.**

**Codex에서 그대로 가져올 것 (내 계획보다 나음):**
1. **`SessionContextExtensions` → `PacketSendExtensions` 교정.** `00_context.md:18`이 잘못된 이름을 적었고 Codex만 잡았다. `13_final_plan.md`에 정정해 기록할 것.
2. **재실행 정책.** *"새 서버에서 단일 검증 실행을 수행한다. 재실행은 서버 재시작을 안내한다."* — **내 계획에는 이 문제 자체가 없다.** 리셋 패킷 없이 같은 서버에 `CounterClient`를 두 번 돌리면 값이 4,000이 되어 데모가 FAIL을 출력하는데, 내 계획은 이를 언급조차 하지 않았다. Codex의 안내 방식을 채택한다(리셋 패킷 추가는 비범위 유지).
3. **`checked` 산술.** N·M·기대값 계산을 `checked`로.
4. **타임아웃 처리.** *"단순히 `WaitAsync`만 종료하지 않고 실제 작업 취소와 연결 정리까지 수행한다"* — 내 §7의 `.WaitAsync(TimeSpan)` 단독 사용을 대체한다.
5. **실패 테스트 2종:** "조회 중 서버 종료", "무응답 타임아웃".
6. **임시 포트 재바인딩 제한적 재시도** (`GetFreePort()` TOCTOU 완화).

**내 계획에서 가져올 것:**
7. **P-C3 해소:** 테스트를 `테스트명 + 부하 파라미터 + 정확한 단언식` 표로 확정 (내 §8-1~8-3 형식).
8. **P-C6 해소:** `.sln` 프로젝트당 12행 구성 확인 절차 (`ClaudeCodeStudy.sln:23-34` 패턴 복제).
9. **P-C2 해소:** `CLAUDE.md` **및 `AGENTS.md`** 양쪽에 예제 목록 2줄 + 플랜 문서 목록 표 1행. (내 계획도 `AGENTS.md`를 빠뜨렸으므로 양측 교정.)
10. **미지 패킷 테스트 Id는 250** 사용 (예약 ID `0xFFFE`/`0xFFFF` 금지 — 단, 그 근거는 위 「자기 수정」대로 교정된 형태로 기록할 것).
11. **P-C4 해소:** 예제 재사용 단위는 `public`, `InternalsVisibleTo` 미사용 (`EchoExample.Tests.csproj:18` 관례).

**신규 결정 필요:**
12. **P-C1 해소:** "완료 확인 절차" 테스트를 seam 명시(`Func<ValueTask>? OnBeforeApply`, 프로덕션 `null`) + 단언 변경("지연된 op까지 최종값에 반영된다")으로 바꾸거나, 항목을 삭제한다. 현재 형태로는 구현 불가.
13. **③ 손상·미지 패킷 정책 택일.** 코드 근거로 우열이 없으므로 오케스트레이터가 정한다. (학습 목적 관점에서는 드롭+카운터+세션 유지를 권하나 강한 주장은 아니다.)

### 판정

**High 0건. Med 3건은 모두 계획 문서 수정만으로 해소된다.** Codex 계획은 코드 근거가 정확하고 실패 경로 설계가 충실하며, 독립 작성된 내 계획과 핵심 결정 7개에서 수렴했다. 위 13개 병합 항목을 반영하면 **APPROVE 가능**하다.
