# Cross-Verify 설치·실행 가이드

## 사전 요구사항

| 항목 | 확인 명령 | 비고 |
|------|----------|------|
| Codex CLI | `codex --version` | `npm install -g @openai/codex` (v0.154+ 검증됨) |
| Codex 인증 | `codex login status` | "Logged in" 아니면 `codex login` |
| pwsh 7+ | `pwsh -v` | invoke-codex.ps1 실행에 필요 |
| .NET 10 SDK | `dotnet --version` | 테스트 실행 |

호출 스크립트 단독 점검 (하네스와 무관하게 Codex 연동만 검증):

```powershell
"1+1의 답만 한 줄로" | Set-Content $env:TEMP\p.md
pwsh -File .claude\skills\cross-verify\scripts\invoke-codex.ps1 -PromptFile $env:TEMP\p.md -OutFile $env:TEMP\o.md -TimeoutSec 120
Get-Content $env:TEMP\o.md, $env:TEMP\o.md.meta.json
```

`status: "success"` meta가 생성되면 연동 정상.

## 실행 방법

Claude Code 세션에서 자연어로 요청한다 (스킬이 자동 트리거):

```
EchoPacket에 SentAtTicks(long) 필드를 교차 검증 개발로 추가해줘
```

또는 부분 재실행:

```
교차 검증 리뷰 단계만 다시 실행해줘
```

## 작은 변경으로 검증하는 사용 예시 (권장 첫 실행)

**요청:** "에코 서버가 빈 메시지를 받으면 `\"(empty)\"`로 바꿔 에코하도록 교차 검증 개발로 수정해줘"

이 정도 크기(파일 1~2개, 테스트 1개)면 전체 파이프라인이 10~20분 내에 완주되고, 산출물 구조를 한눈에 확인할 수 있다:

```
_workspace/cross/20260910_143000_empty-echo/
├── 00_context.md                        ← 요구사항·규칙·기준커밋
├── 10_claude_plan.md                    ← Claude 독립 계획
├── 10_codex_plan.md (+.meta.json)       ← Codex 독립 계획 (실호출 증빙 동반)
├── 11_claude_check_of_codex_plan.md     ← 교환 검토
├── 11_codex_check_of_claude_plan.md (+.meta.json)
├── 12_plan_adjudication.md              ← 의견별 채택/기각/미해결
├── 13_final_plan.md                     ← 통합 계획
├── 14_claude_final_check.md / 14_codex_final_check.md ← VERDICT
├── 20_impl_notes.md / 20_test_results.txt
├── 30_diff.patch / 30_new_files.txt     ← 리뷰 입력 (미커밋 변경 포함)
├── 30_claude_review.md / 30_codex_review.md (+.meta.json)
├── 31_review_adjudication.md
├── 32_fix_notes.md / 32_test_results.txt (수정이 있었던 경우)
├── 33_claude_reverify.md / 33_codex_reverify.md
└── 90_final_report.md                   ← 최종 보고 (한계 명시 포함)
```

## 산출물 읽는 법

- **Codex가 진짜 실행됐는지**: 모든 `*codex*.md` 옆의 `.meta.json`에서 `status: "success"`와 `duration_sec` 확인. meta가 없거나 status가 다르면 그 산출물은 무효이며, 최종 보고의 "Codex 호출 이력"에 실패로 집계되어야 한다.
- **의견이 어떻게 조정됐는지**: `12_plan_adjudication.md`, `31_review_adjudication.md`의 채택/기각/미해결 표.
- **무엇이 검증되지 않았는지**: `90_final_report.md`의 "검증 범위의 한계" 섹션.

## 한계

- Codex 호출은 ChatGPT 요금제 사용량을 소모한다. 무거운 단계(전체 리뷰)는 회당 수 분 걸린다.
- Codex는 read-only 샌드박스라 테스트를 직접 실행하지 못한다 — 테스트 실행 근거는 항상 구현자(Claude)의 `*_test_results.txt`이며, 최종 보고에 이 한계를 명시한다.
