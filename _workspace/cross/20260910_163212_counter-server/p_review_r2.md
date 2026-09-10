# Codex 프롬프트 템플릿 — 독립 코드 리뷰 (Phase 3 / 1차)

어댑터는 Claude 측 리뷰를 포함하면 안 된다.

---

너는 이 리포지토리의 구현 결과를 독립 리뷰하는 역할이다. 다른 모델의 리뷰가 별도로 진행 중이며 서로 교차 검증할 예정이다. 네 판단만 작성하라.

## 확정 계획 (구현이 따라야 했던 기준)
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

확정 해석: F-1 예제 타입 public·IVT 미사용, F-2 응답 16B

## 기준 커밋과 변경 범위
- 기준 커밋: 796aa672673a6a7e101fcf9aa3973b8ee3796097
  (기준: b005dab 직후 상태를 현재 HEAD와 비교. 카운터 기능 전체 + 리뷰 수정이 이 diff에 담김.)
- 변경 diff (커밋되지 않은 변경·신규 파일 포함): _workspace/cross/20260910_163212_counter-server/30_diff_r2.patch 파일을 직접 읽어라 (2,326줄 — 카운터 기능 전체가 이 diff에 있다). 신규 파일 목록: 모두 커밋됨 5c949ae+796aa67
- 관련 파일은 리포에서 직접 열어 전체 맥락을 확인하라.

## 테스트 실행 결과 (구현자 제공)
================================================================================
32_test_results.txt — 리뷰 수정 라운드 재검증 결과 (구현자)
대상 run: 20260910_163212_counter-server
수정 지적: R-C1, R-C2, R-C3, R-C6(보고), R-C7
게이트: 기존 통과 유지 + 신규 포함 전량 통과 + 실패 0
================================================================================

── 환경 ────────────────────────────────────────────────────────────────────
OS: Windows 11, .NET 10, 구성 Release
변경 파일: CounterServer/CounterHandler.cs, CounterClient/CounterScenario.cs,
          CounterExample.Tests/CounterEndToEndTests.cs (제품 동작 변경 없음 — 테스트/주석/dead code)

================================================================================
[1] dotnet build ClaudeCodeStudy.sln -c Release --no-incremental  (클린 빌드)
================================================================================
빌드했습니다.
    경고 10개
    오류 0개

  경고 10건 = 전부 CS0419(cref 모호 참조 'Start'), 모두 ServerLib/Interface/IServerListener.cs.
  → 기존 경고이며 diff 밖 파일이다(이번 변경과 무관, 회귀 아님). 리뷰어 §4.3 정정과 일치.
  (증분 빌드에서는 IServerListener.cs가 재컴파일되지 않아 '경고 0개'로 표시되므로,
   클린 빌드 수치를 대표값으로 기록한다.)

================================================================================
[2] dotnet test CounterExample.Tests/CounterExample.Tests.csproj -c Release --no-build
    (5회 반복 — 리뷰어의 사전 안정성 기준 5× 매칭; 신규 테스트의 타이밍 구조 안정성 확인)
================================================================================
===== RUN 1 =====
통과!  - 실패:     0, 통과:    27, 건너뜀:     0, 전체:    27, 기간: 1 s - CounterExample.Tests.dll (net10.0)
===== RUN 2 =====
통과!  - 실패:     0, 통과:    27, 건너뜀:     0, 전체:    27, 기간: 1 s - CounterExample.Tests.dll (net10.0)
===== RUN 3 =====
통과!  - 실패:     0, 통과:    27, 건너뜀:     0, 전체:    27, 기간: 1 s - CounterExample.Tests.dll (net10.0)
===== RUN 4 =====
통과!  - 실패:     0, 통과:    27, 건너뜀:     0, 전체:    27, 기간: 1 s - CounterExample.Tests.dll (net10.0)
===== RUN 5 =====
통과!  - 실패:     0, 통과:    27, 건너뜀:     0, 전체:    27, 기간: 1 s - CounterExample.Tests.dll (net10.0)

  전 5회 27/27 통과, 실패 0, flaky 미관측.
  테스트 수: 26 → 27 (R-C2의 Handler_DirectCall_RejectsFramesUnreachableViaTransport 1개 신규 추가).

── 수정 대상 2개 테스트 단독 실행(격리 확인) ─────────────────────────────────
명령: dotnet test ... --filter
  "FullyQualifiedName~Handler_DirectCall_RejectsFramesUnreachableViaTransport|
   FullyQualifiedName~Scenario_UnresponsiveServer_TimesOutAndCleansUp"
통과!  - 실패:     0, 통과:     2, 건너뜀:     0, 전체:     2, 기간: 1 s - CounterExample.Tests.dll (net10.0)

  주: 개편된 test 8(Scenario_UnresponsiveServer_TimesOutAndCleansUp)은 3중 단언으로 경로를 고정한다.
    ① TimeoutException.Message가 RunAsync 고유 문구("경합 카운터 시나리오가 기한")를 포함 →
       RunAsync 내부 기한 경로(배리어 취소 → ThrowIfDeadlineAsync)가 낸 것이지, RunAsync가 매달려
       테스트의 15s WaitAsync 안전망(프레임워크 기본 메시지)이 낸 것이 아님을 판별.
    ② 서버 수신 조회 수 >= 1 + Connections(=3) → 워커가 증감 전량 송신 후 배리어 조회까지 보냈다는 직접 증거.
       (기존 테스트였다면 queryCount=1에서 막혀 실패 — R-C1 공백이 닫혔음을 실증.)
    ③ OnClientDisconnected == Connections → 정리 누락 없음.
  세 단언이 모두 통과 = "워커 배리어 도달 → 기한이 그 지점에서 취소 → 전 연결 정리"가 성립.

================================================================================
[3] dotnet test ClaudeCodeStudy.sln -c Release --no-build  (전체 솔루션)
================================================================================
통과!  - 실패:     0, 통과:    14, 건너뜀:     0, 전체:    14, 기간: 202 ms - EchoExample.Tests.dll (net10.0)
통과!  - 실패:     0, 통과:    27, 건너뜀:     0, 전체:    27, 기간: 1 s - CounterExample.Tests.dll (net10.0)

  합계: 41/41 통과, 실패 0, 건너뜀 0.
  기존 EchoExample.Tests 14/14 유지(회귀 없음).

================================================================================
게이트 판정: PASS
  - 기존 통과 유지: O (EchoExample 14/14, Counter 기존 26개 전부 유지)
  - 신규 포함 전량 통과: O (신규 R-C2 테스트 포함 27/27)
  - 실패 0: O
  - 빌드 0오류: O
================================================================================

## 지시
다음 축으로 리뷰하라: 버그(정확성), 회귀 가능성, 보안, 성능(핫패스 할당·블로킹), 요구사항 충족(확정 계획 대비), 테스트 누락.

각 지적은 반드시 이 형식으로: `[R-X#] 심각도(High/Med/Low) | 파일:라인 | 발생 조건 | 영향 | 근거 | 수정 방향`

- 코드 근거 없는 지적은 내지 마라. 동시성 주장은 재현 조건을 구체적으로 기술하라.
- 스타일 선호는 [취향]으로 분리하고 심각도를 매기지 마라.
- 계획에 없는 변경(범위 이탈)이 diff에 있으면 별도 섹션으로 지적하라.

마지막에 `## 종합`: High 지적 수, 병합 가능 여부에 대한 의견. 한국어로 작성하고 파일을 수정하지 마라.
