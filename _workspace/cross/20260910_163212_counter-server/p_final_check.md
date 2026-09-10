# Codex 프롬프트 템플릿 — 통합 계획 재검토 (Phase 1 / 확정 전)

---

두 모델의 계획과 상호 검토를 반영해 통합된 최종 계획이 작성되었다. 구현 착수 전 마지막 검토다.

## 요구사항 및 컨텍스트
# 00_context — 경합 카운터 서버 (교차 검증 개발)

## 사용자 요구사항 (원문)
> 간단하게 테스트용 프로젝트로 경합이 일어나는 서버 만들어봐. 예를들면 하나의 변수 더하기 패킷, 빼기 패킷을 만든다던가 하는식

## 해석된 요구사항
- **목적:** 여러 클라이언트가 동시에 하나의 공유 변수(카운터)를 더하기/빼기 패킷으로 갱신하는 **경합(contention) 시연·학습용 예제**. cross-verify 하네스의 첫 실전 검증 대상이기도 하다.
- 서버: 공유 카운터 1개. 더하기 패킷·빼기 패킷 수신 시 카운터 갱신. 다중 세션 동시 유입 = 경합 지점.
- 클라이언트: 여러 스레드/연결이 더하기·빼기를 동시 다발 송신해 경합을 실제로 유발하고, 최종 값이 기대값과 일치하는지 확인할 수 있어야 한다 (경합 처리가 올바르면 결정적 기대값이 나온다는 것이 학습 포인트).
- 규모: **작게.** EchoServer/EchoClient 수준의 학습용 예제 + 테스트.

## 기준 커밋
`3aa7a80205ae84f96f6fe780a3cbde255178faf0` (워킹트리 깨끗)

## 기존 자산 (중요 — 재사용 검토 대상)
- **`IncrementPacket`(Id=3)·`DecrementPacket`(Id=4)이 ServerLib에 이미 존재한다** (`ServerLib/Core/Serialization/Packets/`). 본문 없는 struct, "서버의 test 변수를 1 증감"이라는 XML 주석. 현재 이 패킷을 사용하는 예제·테스트는 없다. 재사용할지, 수량 필드가 필요해 새 패킷을 만들지는 계획에서 판단하라 (기존 Id 1~17, 0xFFFE/0xFFFF는 사용 중 — 신규 Id는 충돌 금지).
- 예제 패턴: `EchoServer/Program.cs`(리스너·OnReceived·역직렬화·응답), `EchoClient/Program.cs`(CreateClient·ConnectAsync·await using), 루프백 E2E 테스트 패턴: `EchoExample.Tests/EchoEndToEndTests.cs`(GetFreePort·TaskCompletionSource·WaitAsync 타임아웃).
- 라이브러리 진입점: `ServerNet.CreateListener()`/`CreateClient()`만 사용(Transport 구현체는 internal). 직렬화: `BinaryPacketSerializer`, `PacketPool`(헤더 4B `[Id(2)|BodyLength(2)]`), `SessionContextExtensions.SendAsync<T>` 확장.
- 솔루션: `ClaudeCodeStudy.sln` (.NET 10). 기존 테스트 14개 전부 통과 상태.

## 프로젝트 규칙 (CLAUDE.md 발췌 — 반드시 준수)
1. Interface는 순수 추상화만, Core는 구현만. 외부 소비자는 ServerNet 팩토리 인터페이스로만 사용.
2. 모든 public API·인터페이스에 상세 XML 문서 주석: Thread Safety·Memory Allocation(소유권/생명주기)·Blocking 여부 필수.
3. 네트워크·메모리 관련 선언부(Socket, Channel<T>, ArrayPool, Interlocked 대상 필드, SemaphoreSlim 등)에는 **내부 동작 메커니즘을 근거로 한 인라인 주석** 필수.
4. 동시성: lock-free(Interlocked·Channel) 우선. 전통 락 사용 시 정당화 주석 필수.
5. 새 기능 추가 시 예제(Program.cs)가 라이브러리 사용 예제 역할을 해야 한다.
6. 테스트는 xUnit, `dotnet test`로 실제 실행.

## 제약조건
- 서버 바인딩은 루프백(127.0.0.1) 전용, 포트는 기존 예제와 충돌하지 않게 (9000=Echo, 8080=EchoWeb 사용 중).
- 클라이언트 최종 검증이 가능해야 함: 총 N회 더하기·M회 빼기 후 카운터 == N−M 확인 경로 필요 (조회 패킷 또는 서버 응답 — 방식은 계획에서 결정).
- 솔루션에 새 프로젝트 추가 시 `ClaudeCodeStudy.sln`에 등록.
- 테스트는 기존 `EchoExample.Tests`에 추가하거나 새 테스트 프로젝트 — 계획에서 결정하되 근거 제시.

## 비범위 (non-goals)
- 모니터링·인증·티켓팅·웹 UI 연동 없음.
- 새 Transport·프로토콜 확장 없음 (기존 ServerLib public API만 사용).
- 벤치마크/성능 측정 하네스 없음 (경합 정확성 시연이 목적).


## 의견 조정 결과 (채택/기각/미해결 기록)
# 12_plan_adjudication — Plan 의견 조정 (라운드 1)

조정자: 오케스트레이터(메인 세션). 입력: 10_claude_plan.md, 10_codex_plan.md, 11_claude_check_of_codex_plan.md, 11_codex_check_of_claude_plan.md (meta status=success 확인).

## 합의 확인 (양측 독립 수렴 — 쟁점 아님)
Id=3·4 재사용 / 조회·응답 패킷 Id=18·19 신설 / 조회 본문 0B / 패킷 위치 ServerLib Packets / 헤더 id 기준 라우팅(Deserialize<T>가 id 미검증이므로) / 세션 순차 디스패치(`SocketPipelineSession.cs:197`)에 기반한 "연결별 완료 확인 후 최종 조회" 배리어 / 신규 `CounterExample.Tests` / Interlocked 기반 공유 상태.

## 조정 결정

| # | 쟁점 (출처) | 판정 | 근거 |
|---|---|---|---|
| D1 | 통합 베이스 계획 | **Codex 계획 채택** | 양측 동의. 범위가 작고(사용자 "간단하게") 실패 처리·실행 전제(서버 재시작)·시나리오 공유가 구체적. Claude 검토도 "재실행 정책은 내 계획에 없던 구멍" 인정 |
| D2 | unsafe(비원자) 토글 (Claude안 핵심 / Codex P-X7) | **기각 — 이번 범위 제외** | 요구는 "경합 발생 + 올바른 최종값"이며 P-X7 타당(과도 설계·SpinWait이 IO 스레드 직렬 정지). Claude 검토도 심각도 미부여·리스크 인정. **후속 확장 후보로 최종 보고에 기록** |
| D3 | 응답 본문 8B vs 16B | **16B 채택** (`Value`+`AppliedOps`, AppliedOps는 항상 Interlocked) | Codex 종합이 직접 권장("상쇄된 누락을 보완하는 진단값"). 8+8=16B, `checked` 기대값 계산과 함께 상쇄 은폐 방지 |
| D4 | Codex §6 "마지막 명령 지연" 테스트 (Claude P-C1: 자기모순) | **채택(P-C1) — 해당 테스트 항목 삭제** | 순차 디스패치 구조상 마지막 op 지연은 같은 연결의 조회도 지연시켜 의도를 증명 못함 + seam 파일이 §2에 없음. 배리어 정확성은 시나리오 공유(E2E가 실제 `CounterScenario` 실행) + 상태 동시성 테스트로 커버 |
| D5 | 클라 무응답·실패 종료 절차 (Codex P-X1) | **채택** | 베이스 계획 §5에 이미 구체화됨(전체 기한·취소 전파·전 연결 정리·비영 종료 코드) |
| D6 | 문서 갱신 누락 (Claude P-C2) | **채택** | 예제 목록 2줄 + plan 문서 표 1행을 **CLAUDE.md와 AGENTS.md 양쪽**에 추가 |
| D7 | 테스트 규모·기대값 명시 (Claude P-C3) | **채택** | 8연결 × (+1,000 / −750) = 최종 2,000, `checked` 계산, 타임아웃 수치 명시(베이스 계획 수치 유지) |
| D8 | 00_context 오기 (Codex 교정) | **채택** | `SessionContextExtensions` → 실제 `PacketSendExtensions.SendAsync<T>` |
| D9 | 조회 경로 주석 분리 (Codex P-X4) | **채택** | 증감=동기 완료 ValueTask / 조회=비동기 완료·조건부 할당 가능 — XML 주석에 정확히 반영 |
| D10 | 조회 송신 실패 처리 (Codex P-X5) | **채택** | SocketException=상대 종료 가정 금지(송신 타임아웃도 TimedOut 변환됨, `SocketPipelineSession.cs:375`) → 로그 + 해당 세션 종료 |
| D11 | 루프백 명시 (Codex P-X6) | **채택** | `listener.Start(port, IPAddress.Loopback)` — EchoServer 패턴 |
| D12 | 서버 종료 시 요약 출력 (Codex P-X8) | **채택** | 종료 출력은 참고용 `Value`만. 검증 판정은 클라이언트 배리어 이후 조회로 한정 |
| D13 | F6 교정 (Codex P-X9 + Claude 자기 수정) | **채택** | 서버는 정상 PING만 가로챔 — Id=250 미지 패킷 테스트는 유지, 근거 문장만 교정 |
| D14 | 워킹트리 서술 (Codex 진술) | **기각(사실 교정)** | `.claude/settings.local.json`은 전역 ignore 대상 — `git status --porcelain` 빈 출력 확인됨. 리뷰 범위 영향 없음 |
| D15 | 포트 9300 vs 9010, 패킷 명명 | **[취향] 베이스 유지** | 9300, `CounterQueryPacket`/`CounterValuePacket` |
| D16 | Claude [미해결 질문] 2건 | **해소** | unsafe 기각으로 초기 모드 질문 소멸. 클라이언트는 비인터랙티브 일괄 실행 + 종료 코드(베이스 계획) |

## 미해결
없음 (High 쟁점 0건, 전 항목 판정 완료).

## 통계
Codex→Claude 지적 9건: 채택 8(P-X1·2·3부분·4·5·6·7·8·9) / 부분채택 1(P-X3: 시나리오 공유 채택, 지연 테스트는 D4로 삭제). Claude→Codex 지적 7건: 채택 3(P-C1·2·3) / Low 4건 중 채택 2(sln 12행 경고, F6 유형) · 기각 1(워킹트리, D14) · 취향 처리 1.


## 검토 대상: 통합 최종 계획
# 13_final_plan — 경합 카운터 서버 통합 최종 계획 (라운드 1)

**구성: `10_codex_plan.md`를 베이스로 채택하고(D1), 아래 수정 사항을 적용한 것이 최종 계획이다.** 구현자는 두 문서를 함께 읽되, 충돌 시 본 문서가 우선한다. 각 수정의 근거는 `12_plan_adjudication.md`의 D번호.

## 베이스 계획 요약 (10_codex_plan.md)
- `IncrementPacket`(Id=3)·`DecrementPacket`(Id=4) 재사용, `CounterQueryPacket`(Id=18, 본문 0B)·`CounterValuePacket`(Id=19) 신설.
- 신규 프로젝트 3개: `CounterServer`(9300, 루프백), `CounterClient`, `CounterExample.Tests` + sln 등록.
- 서버: 인스턴스 `CounterState`(Interlocked), `CounterHandler`(헤더 id 라우팅 + 길이 검증 — `Deserialize<T>`가 id를 검증하지 않으므로 필수), Program은 EchoServer 패턴.
- 클라: `CounterScenario.RunAsync` 실행기(Program과 E2E 테스트가 공유), 8연결 × (+1,000/−750), 공통 시작 신호 → 연결별 마지막 명령 후 조회(=그 연결 처리 완료 배리어) → 전 연결 확인 후 최종 조회 → 기대값 2,000 검증, PASS/FAIL + 종료 코드.
- 실행 전제: 새 서버 + 단일 클라 1회 실행. 재실행은 서버 재시작 안내. `checked` 산술.
- 실패 처리: 전체 기한·취소 전파·전 연결 정리·비영 종료 코드, 잘못된 패킷은 길이·id 검증으로 차단.

## 베이스 대비 수정 사항 (전부 반영 필수)

1. **[D3] 응답 16B:** `CounterValuePacket` = `long Value` + `long AppliedOps` (16B). `CounterState`에 `AppliedOps`(적용된 증감 연산 총수) 추가 — **항상** `Interlocked.Increment`로 집계. 클라 최종 검증은 `Value == 8×(1000−750) = 2,000` **및** `AppliedOps == 8×(1000+750) = 14,000` (둘 다 `checked`). 두 값의 쌍은 비원자이므로 "정지 상태(배리어 이후) 조회"에서만 함께 단언한다는 주석 필수.
2. **[D4] 테스트 항목 삭제:** 베이스 §6 "완료 확인 절차 — 마지막 명령을 테스트용 비동기 신호로 지연" 항목은 구현하지 않는다(순차 디스패치 구조상 자기모순). 대신 E2E는 실제 `CounterScenario`를 호출해 배리어 경로 자체를 검증한다.
3. **[D6] 문서 갱신:** `CLAUDE.md`·`AGENTS.md` 모두에 예제 목록 항목(CounterServer 1줄·CounterClient 1줄)과 plan 문서 표에 `plan/contention_counter_0910.md` 1행 추가.
4. **[D9] XML 주석 정밀화:** 증감 경로 "동기 갱신 후 완료된 ValueTask, 무할당" / 조회 응답 경로 "송신 미완료 시 비동기 완료·조건부 할당 가능(`PacketSendExtensions.cs:78-88`)"으로 구분 서술. Blocking·Memory Allocation 주석에 그대로 반영.
5. **[D10] 조회 응답 송신 실패:** `SocketException`을 "상대 종료"로 단정하지 않는다(송신 타임아웃도 TimedOut으로 변환됨). 로그 후 해당 세션 종료. 자동 재송신 금지.
6. **[D12] 서버 종료 출력:** 아무 키 종료 시 참고용 `Value`만 출력(검증 판정 아님 — 정지 보장 없음).
7. **[D13] 미지 패킷 테스트:** Id=250 사용 유지. 근거 문장은 "서버 측은 정상 PING(0xFFFE)만 하트비트로 가로채며 그 외는 앱 경로 도달"로 교정.
8. **[D7] 수치 고정:** 연결 8, +1,000/−750, 기대 Value 2,000·AppliedOps 14,000, E2E 타임아웃은 기존 테스트 관례(5초) 또는 부하 규모 고려 시 30초 상한 — 구현자가 실측으로 결정하되 수치를 테스트 상수로 명시.
9. **[sln 함정]** `dotnet sln add` 후 구성 매핑이 기존 프로젝트(프로젝트당 12행: Any CPU/x64/x86 × Debug/Release)와 일치하는지 확인, 누락 시 수동 보완.
10. **[InternalsVisibleTo]** 예제 내부 타입(`CounterState`·`CounterHandler`·`CounterScenario`)을 테스트에서 쓰기 위한 접근 허용은 `EchoExample.Tests` 참조 관례(기존 csproj 방식)를 그대로 따른다.

## 테스트 전략 (확정)
- 패킷: Id·본문 길이(0/0/0/16)·`CounterValuePacket` 왕복(0·음수·`long.MinValue/MaxValue`).
- 상태: 실제 `CounterState` 동시 갱신 — 더하기만/빼기만/불균형 혼합, `Value`·`AppliedOps` 동시 단언.
- E2E(실제 소켓·실제 `CounterScenario`): 초기값 0 조회 / 단일 연결 증감 / 8연결 배리어 후 최종값·AppliedOps / 총 연산 0.
- 실패: 잘못된 본문 길이 → 상태 불변 + 해당 연결만 영향, 미지 Id=250, 조회 중 서버 종료, 무응답 타임아웃(실제 작업 취소·정리 포함).
- 실행: `dotnet build` → `dotnet test CounterExample.Tests` → `dotnet test ClaudeCodeStudy.sln`(기존 14개 유지 + 신규 전량 통과 + 실패 0).

## 게이트
기존 테스트 전량 유지·신규 전량 통과·실패 0. 설계 문서 `plan/contention_counter_0910.md` 작성(베이스 §2 목록에 포함).


## 지시
1. 조정 결과에서 "채택"된 의견이 통합 계획에 실제로 반영되었는지 항목별로 대조하라.
2. 통합 과정에서 새로 생긴 모순·누락이 있는지 확인하라.
3. 남은 미해결 항목 중 구현을 막을 만큼 중대한 것이 있는지 판정하라.

마지막 줄에 반드시 `VERDICT: APPROVE` 또는 `VERDICT: REQUEST-CHANGES`를 출력하고, REQUEST-CHANGES면 바로 위에 사유를 번호 목록으로 기술하라. 한국어로 작성하고 파일을 수정하지 마라.


## 참고 — 베이스 계획 원문 위치
최종 계획은 베이스 계획을 참조 채택한 형태다. 베이스 계획은 리포의 _workspace/cross/20260910_163212_counter-server/10_codex_plan.md 에서 직접 읽어라.