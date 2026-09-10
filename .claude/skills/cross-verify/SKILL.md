---
name: cross-verify
description: >
  Claude Code와 실제 Codex CLI가 Plan·최종 코드리뷰를 교차 검증하는 개발 파이프라인 오케스트레이터.
  트리거: '교차 검증 개발', '교차 검증으로 구현', 'cross-verify', '크로스 체크로 개발', 'Codex랑 같이 구현',
  'Codex와 교차 검증', '더블 체크 개발', '교차 리뷰 파이프라인'. 후속: '교차 검증 이어서', '교차 검증 재실행',
  '리뷰 단계만 다시', 'Plan 검증만 다시', '이전 교차 검증 결과 기반으로'. 기능 구현·수정 요청에 이 표현들이
  결합되면 반드시 이 스킬을 사용할 것. 단발 Codex 질문·단독 리뷰는 codex 스킬이 담당하므로 트리거하지 않는다.
---

# Cross-Verify — Claude ↔ Codex 교차 검증 개발 파이프라인

**실행 모드: 서브 에이전트** (에이전트 팀 아님 — 1차 산출 단계에서 상대 결론을 보기 전 독립 작성이 절대 요구라, 에이전트 간 직접 통신을 구조적으로 차단하고 파일 기반으로만 데이터를 전달한다.)

**아키텍처: 파이프라인(Plan→구현→리뷰) × 생성-검증(각 단계에서 Claude·Codex 상호 검증)**

```
[오케스트레이터(메인 세션)]
  Phase 1: cross-planner ∥ codex-adapter(plan)   → 교환 검토 → 조정 → 통합 계획 → 양측 재검토
  Phase 2: cross-implementer                      → 테스트 실행 (범위 이탈 시 Phase 1 회귀)
  Phase 3: cross-reviewer ∥ codex-adapter(review) → 교환 검증 → 조정 → 수정 → 양측 재검토
```

핵심 불변식:
- **Codex는 실제 CLI 호출로만 참여한다.** Claude 에이전트가 Codex 역할을 흉내 내는 것은 금지. 모든 Codex 산출물 옆에는 `invoke-codex.ps1`이 남긴 `*.meta.json`(status=success)이 있어야 하며, 없으면 그 산출물은 무효다.
- **Codex는 검증만 한다.** 프로젝트 코드 수정은 cross-implementer만 수행한다 (Codex는 read-only 샌드박스 고정).
- **두 모델의 동의는 통과 조건이 아니라 필요 조건이다.** 최종 판정은 코드와 실제 테스트 실행 결과로 한다.

## Phase 0: 컨텍스트 확인 및 사전 점검

1. **실행 모드 판별:**
   - `_workspace/cross/` 에 기존 run 존재 + 사용자가 부분 재실행 요청("리뷰만 다시" 등) → 해당 Phase만 재실행 (기존 run 디렉토리 재사용, 산출물은 `_r2` 접미사)
   - 기존 run 존재 + 새 기능 요청 → 새 run 디렉토리 생성
   - 미존재 → 초기 실행
2. **Codex 사전 점검 (필수):** `codex login status` 실행. 실패하면 **여기서 중단**하고 사용자에게 `codex login` 필요를 보고한다. Codex 없이 진행하며 "교차 검증"이라 부르는 것은 금지 — 사용자가 명시 승인하면 "Claude 단독 (교차 검증 아님)"으로 진행·보고한다.
3. run 디렉토리 생성: `_workspace/cross/<YYYYMMDD_HHmmss_기능약칭>/`
4. 기준 커밋 기록: `git rev-parse HEAD` → 컨텍스트 문서에 포함. 워킹트리가 더럽면 사용자에게 알리고 계속할지 확인한다(리뷰 범위 오염 방지).

## Phase 1: Plan 교차 검증 (최대 3라운드)

1. **컨텍스트 작성** — 오케스트레이터가 `00_context.md` 작성: 사용자 요구사항(원문), 프로젝트 규칙 요약(CLAUDE.md 관련 조항), 관련 코드 경로와 현재 구조 요지, 제약조건, 기준 커밋, 비범위.
2. **독립 계획 (병렬)** — 동일 입력, 상대 산출물 미노출:
   - `Agent(cross-planner, model: "opus", run_in_background: true)` mode=plan → `10_claude_plan.md`
   - `Agent(codex-adapter, model: "opus", run_in_background: true)` 단계=plan (`prompts/plan.md` 템플릿) → `10_codex_plan.md` + meta
3. **교환 검토 (병렬)** — 이제 서로의 계획을 노출:
   - cross-planner mode=check → `11_claude_check_of_codex_plan.md`
   - codex-adapter 단계=plan-check (`prompts/plan_check.md`) → `11_codex_check_of_claude_plan.md` + meta
4. **의견 조정** — 오케스트레이터가 `12_plan_adjudication.md` 작성: 양측 지적 전수 목록화, 의견별 `채택/기각/미해결` + 근거. 요구사항 누락·잘못된 가정·과도한 설계·기존 구조 충돌·예외 처리·테스트 전략 6축 모두 다뤘는지 확인.
5. **통합 계획** — `13_final_plan.md` 작성 (채택 의견 반영 내역 명시).
6. **양측 재검토 (병렬)** — cross-planner mode=final-check → `14_claude_final_check.md`; codex-adapter 단계=final-check (`prompts/final_check.md`) → `14_codex_final_check.md`. 양측 APPROVE면 Phase 2로.
7. **라운드 한도:** REQUEST-CHANGES면 조정→통합→재검토를 반복하되 최대 3라운드. 이후에도 중대한 의견 충돌이 남으면 **중단하고 사용자에게 보고**: 충돌 쟁점, 양측 근거, 선택지를 제시하고 결정을 받는다.

## Phase 2: 구현과 테스트

1. `Agent(cross-implementer, model: "opus")` — 입력: `00_context.md`, `13_final_plan.md`. 출력: 코드 변경 + `20_impl_notes.md`, `20_test_results.txt`(실제 `dotnet test` 실행 결과).
2. **범위 이탈 처리:** `20_impl_notes.md`에 `[범위 이탈]` 섹션이 있으면 구현을 멈춘 상태다 → 해당 변경점을 `00_context.md`에 반영하고 **Phase 1로 회귀**한다 (경량 회귀: 이탈 항목만 양측 검토).
3. 테스트 실패 시 implementer가 3회까지 수정 시도, 그래도 실패면 사용자 보고.

## Phase 3: 최종 코드 리뷰 교차 검증 (최대 3라운드)

1. **리뷰 입력 고정** — 오케스트레이터가 준비:
   - `git rev-parse HEAD` + 기준 커밋으로 `git diff <base> > 30_diff.patch` (**커밋되지 않은 변경 포함**), `git status --porcelain`으로 신규(untracked) 파일 목록화, 신규 파일은 diff에 안 잡히므로 `30_new_files.txt`에 경로+요지 기록.
   - 테스트 결과·확정 계획 경로 정리.
2. **독립 리뷰 (병렬)** — 동일 입력, 상대 리뷰 미노출:
   - `Agent(cross-reviewer, model: "opus", run_in_background: true)` mode=review → `30_claude_review.md`
   - `Agent(codex-adapter, model: "opus", run_in_background: true)` 단계=review (`prompts/review.md`, 타임아웃 900s) → `30_codex_review.md` + meta
3. **상호 검증 (병렬)** — cross-reviewer mode=adjudicate → `31_claude_adjudication.md`; codex-adapter 단계=adjudicate (`prompts/adjudicate.md`) → `31_codex_adjudication.md` + meta.
4. **조정** — 오케스트레이터가 `31_review_adjudication.md` 확정: 지적별 유효/기각/미해결 + 근거, 중복 통합, 취향([취향])과 결함 분리. 미해결 항목은 코드를 직접 확인해 판정하고, 판정 불가면 미해결로 남겨 최종 보고에 승계.
5. **수정** — 유효 지적이 있으면 `Agent(cross-implementer)` 재호출 → `32_fix_notes.md`, `32_test_results.txt`.
6. **재검토** — **코드가 바뀌었으므로 이전 승인은 무효.** cross-reviewer mode=reverify + codex-adapter 단계=reverify (`prompts/reverify.md`) 병렬 실행 → `33_*_reverify.md`. 양측 APPROVE + 테스트 통과면 종료.
7. **라운드 한도:** 수정→재검토 최대 3라운드. 초과 시 남은 쟁점·근거·선택지를 사용자에게 제시.

## 종료 및 실패 처리

**완료 조건 (전부 충족):**
- High 심각도의 미해결 결함 0건
- 필수 테스트 실제 실행·통과 (`*_test_results.txt` 근거)
- 양측 최종 VERDICT: APPROVE **+ 각 Codex 산출물의 meta.json status=success 전수 확인**

**최종 보고 (`90_final_report.md` + 사용자 요약)에 반드시 포함:**
- 구현 요약, 채택/기각/미해결 의견 통계와 미해결 목록
- 실행한 테스트와 결과, **실행하지 못한 테스트와 검증 범위의 한계**
- Codex 호출 이력 (단계별 status/duration — meta.json 집계)

**실패 구분 (혼동 금지):**
| 상황 | 보고 |
|------|------|
| Codex 호출 실패(인증/타임아웃/빈 출력) 후 재시도도 실패 | "Codex 미실행 — 교차 검증 미완료". 절대 "검증 통과"로 보고하지 않음. 사용자에게 진행 여부 확인 |
| 3라운드 후 의견 충돌 잔존 | "미합의" — 쟁점·양측 근거·선택지 제시 |
| 테스트 실패 잔존 | "미완료" — 실패 테스트 원문 첨부 |

## 데이터 전달 프로토콜

파일 기반 (서브 에이전트 모드): `_workspace/cross/<run-id>/` 산출물 번호 규칙 — `0x`=컨텍스트, `1x`=Plan 단계, `2x`=구현, `3x`=리뷰 단계, `9x`=최종. Codex 산출물은 항상 짝 파일 `*.meta.json` 동반. 라운드 2+는 `_r2` 접미사. 중간 파일은 삭제하지 않고 보존한다(감사 추적).

## 에러 핸들링

- 에이전트 실패: 1회 재호출, 재실패 시 해당 단계 실패로 사용자 보고 (부분 결과는 workspace에 보존).
- Codex 관련 실패: codex-adapter의 에러 표 참조 (인증 오류는 재시도 없이 즉시 보고).
- 충돌 의견은 삭제하지 않고 출처(Claude/Codex) 병기로 기록한다.

## 테스트 시나리오

**정상 흐름:** "EchoPacket에 타임스탬프 필드를 교차 검증으로 추가해줘" → Phase 0 점검 통과 → 양측 독립 계획 → 교환·조정·통합 → 구현+테스트 → 양측 독립 리뷰 → 조정·수정·재검토 → APPROVE×2 + 테스트 통과 → 최종 보고.

**에러 흐름 1 (Codex 인증 만료):** Phase 0의 `codex login status` 실패 → 즉시 중단, "codex login 후 재실행" 안내. 교차 검증 완료 보고 없음.

**에러 흐름 2 (리뷰 3라운드 초과):** High 지적 1건이 3라운드 후에도 유효/기각 미합의 → 쟁점 요약 + Claude 근거 + Codex 근거 + 선택지(수정안 A/B/보류)를 사용자에게 제시하고 대기.

## 설치·실행 방법과 사용 예시

`references/usage.md` 참조 (사전 요구사항 점검, 작은 변경으로 검증하는 실행 예시, 산출물 읽는 법).
