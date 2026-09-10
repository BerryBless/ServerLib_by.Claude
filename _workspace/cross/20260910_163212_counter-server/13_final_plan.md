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
