---
name: concurrency-guard-orchestrator
description: ".NET 10 고성능 비동기 서버 라이브러리의 동시성·락·데드락을 종합 감사하는 오케스트레이터. Lock-Free 설계 강제, 락 정당화 주석 감사, 데드락 생성-검증 분석을 에이전트 팀으로 조율하고 단일 보안 리포트를 생성한다. 트리거: '동시성 검사', '락 감사', '데드락 분석', '동시성 리뷰', 'Lock-Free 검증', 'async 데드락', 'concurrency guard', '컨텐션 분석'. 후속 작업: '다시 분석', '데드락만 재검토', '락 정당화 재감사', '이전 결과 업데이트', '보완 분석'."
---

# Concurrency Guard Orchestrator

.NET 10 고성능 비동기 서버 라이브러리를 위한 동시성 전문 감사 팀을 조율하는 오케스트레이터.

## 실행 모드: 하이브리드 에이전트 팀

| Phase | 모드 | 팀 구성 | 이유 |
|-------|------|---------|------|
| Phase A (병렬 락 감사) | 에이전트 팀 | lock-free-enforcer ↔ lock-justification-auditor | 두 에이전트가 락 목록 공유·조율 필요 |
| Phase B (생성-검증) | 에이전트 팀 (동일 팀) | deadlock-analyzer → deadlock-reviewer | 분석기-검증자 직접 SendMessage 교환 |

세션당 팀 1개 제약 → 4개 에이전트를 단일 팀으로 구성하고 `depends_on`으로 Phase A/B 순서를 모델링한다.

## 에이전트 구성

| 팀원 | 에이전트 타입 | 스킬 | 출력 |
|------|-------------|------|------|
| lock-free-enforcer | lock-free-enforcer | /lock-free-enforcement | `02_lockfree_findings.json` |
| lock-justification-auditor | lock-justification-auditor | /lock-justification-audit | `02_lockjustification_findings.json` |
| deadlock-analyzer | deadlock-analyzer | /deadlock-static-analysis | `03_deadlock_analysis.json` |
| deadlock-reviewer | deadlock-reviewer | /deadlock-review | `03_deadlock_review.json` |

---

## 워크플로우

### Phase 0: 컨텍스트 확인

1. `_workspace/` 존재 여부 확인
2. 분기:
   - **미존재** → 초기 실행. Phase 1로 진행
   - **존재 + 특정 에이전트 재실행 요청** ("데드락만 다시") → **부분 재실행**: 해당 에이전트만 재호출, Phase 5에서 전체 리포트 재통합
   - **존재 + 새 코드 제공** → **새 실행**: `_workspace/`를 `_workspace_{YYYYMMDD_HHMMSS}/`로 이동 후 Phase 1

### Phase 1: 분석 대상 수집

**케이스 A — 인수 없음 (현재 브랜치 diff):**
```bash
BASE=$(git merge-base HEAD main 2>/dev/null || git merge-base HEAD master 2>/dev/null)
git diff $BASE HEAD
```

**케이스 B — 경로 지정:**
```bash
# .cs 파일들을 읽어 단일 파일로 병합
find <path> -name "*.cs" -exec cat {} \; > combined_source.txt
```

**케이스 C — PR 번호:**
```bash
gh pr diff <PR번호>
```

수집 내용을 `_workspace/00_input/source.txt`에 저장한다.
소스가 비어있으면 사용자에게 알리고 중지한다.

### Phase 2: 팀 구성

```
TeamCreate(
  team_name: "concurrency-guard-team",
  members: [
    {
      name: "lock-free-enforcer",
      agent_type: "lock-free-enforcer",
      model: "opus",
      prompt: "당신은 lock-free-enforcer입니다. /lock-free-enforcement 스킬을 사용하여 _workspace/00_input/source.txt를 감사하고 결과를 _workspace/02_lockfree_findings.json에 저장하세요. 완료 후 necessary_locks 목록을 lock-justification-auditor에게 SendMessage로 전달하고, 리더에게 완료를 알리세요."
    },
    {
      name: "lock-justification-auditor",
      agent_type: "lock-justification-auditor",
      model: "opus",
      prompt: "당신은 lock-justification-auditor입니다. /lock-justification-audit 스킬을 사용하여 _workspace/00_input/source.txt를 감사하세요. lock-free-enforcer로부터 necessary_locks 목록을 수신 후 우선 감사하고 결과를 _workspace/02_lockjustification_findings.json에 저장하세요. 완료 후 리더에게 알리세요."
    },
    {
      name: "deadlock-analyzer",
      agent_type: "deadlock-analyzer",
      model: "opus",
      prompt: "당신은 deadlock-analyzer입니다. lock-free-enforcer 완료 알림을 받은 후 /deadlock-static-analysis 스킬을 사용하여 _workspace/00_input/source.txt와 _workspace/02_lockfree_findings.json을 분석하세요. 결과를 _workspace/03_deadlock_analysis.json에 저장하고 deadlock-reviewer에게 검증 요청 SendMessage를 보내세요."
    },
    {
      name: "deadlock-reviewer",
      agent_type: "deadlock-reviewer",
      model: "opus",
      prompt: "당신은 deadlock-reviewer입니다. deadlock-analyzer로부터 검증 요청 SendMessage를 받은 후 /deadlock-review 스킬을 사용하여 _workspace/03_deadlock_analysis.json을 독립 검증하세요. 결과를 _workspace/03_deadlock_review.json에 저장하고 리더에게 완료를 알리세요. 필요하면 deadlock-analyzer에게 재분석 요청(1회 한도)을 보내세요."
    }
  ]
)
```

작업 등록 (의존성으로 Phase A/B 순서 모델링):
```
TaskCreate(tasks: [
  {
    title: "Lock-Free 설계 감사",
    description: "/lock-free-enforcement 스킬로 모든 락 사용을 감사하고 necessary_locks를 lock-justification-auditor에게 전달",
    assignee: "lock-free-enforcer"
  },
  {
    title: "락 정당화 주석 감사",
    description: "/lock-justification-audit 스킬로 [LOCK-REQUIRED] 주석 존재·품질을 감사",
    assignee: "lock-justification-auditor"
  },
  {
    title: "데드락 정적 분석",
    description: "/deadlock-static-analysis 스킬로 async 패턴 분석 후 deadlock-reviewer에게 검증 요청",
    assignee: "deadlock-analyzer",
    depends_on: ["Lock-Free 설계 감사"]
  },
  {
    title: "데드락 분석 검증",
    description: "/deadlock-review 스킬로 분석 보고서 독립 검증 및 최종 확정",
    assignee: "deadlock-reviewer",
    depends_on: ["데드락 정적 분석"]
  }
])
```

### Phase 3: Phase A — 병렬 락 감사

**실행 모드: 에이전트 팀 (팬아웃)**

lock-free-enforcer와 lock-justification-auditor가 병렬로 실행된다.

**팀 통신 흐름:**
```
lock-free-enforcer  →  [necessary_locks 목록]  →  lock-justification-auditor
      ↓                                                      ↓
02_lockfree_findings.json                    02_lockjustification_findings.json
      ↓
  리더에게 완료 알림
```

리더는 두 에이전트의 완료 알림을 수신할 때까지 모니터링한다.

### Phase 4: Phase B — 생성-검증 데드락 분석

**실행 모드: 에이전트 팀 (생성-검증 순환)**

lock-free-enforcer 완료 후 deadlock-analyzer가 시작된다.

**생성-검증 순환:**
```
deadlock-analyzer → [분석 완료] → deadlock-reviewer
                                        ↓
                         [기각/수정/추가 발견]
                                        ↓
                    재분석 필요? → deadlock-analyzer (최대 1회)
                                        ↓
                         [최종 확정] → 리더에게 완료 알림
```

최대 2라운드(초기 분석 + 1회 재분석) 보장.

### Phase 5: 결과 통합 및 리포트 생성

4개 파일을 Read로 수집:
- `_workspace/02_lockfree_findings.json`
- `_workspace/02_lockjustification_findings.json`
- `_workspace/03_deadlock_analysis.json`
- `_workspace/03_deadlock_review.json`

**종합 점수 계산:**
```
lockfree_score         (lock-free-enforcer)
lockjustification_score (lock-justification-auditor)
deadlock_final_score   (deadlock-reviewer의 final_score)

overall = lockfree_score * 0.35
        + lockjustification_score * 0.30
        + deadlock_final_score * 0.35
```

**리포트 형식** (`_workspace/04_concurrency_guard_report.md`):

```markdown
# 동시성 가드 리포트
생성: {datetime} | 대상: {target}

## 종합 건강 점수
| 도메인 | 점수 | Critical | High | Medium | Low |
|--------|------|----------|------|--------|-----|
| 🔓 Lock-Free | XX | N | N | N | N |
| 📝 락 정당화 | XX | — | N | N | N |
| ⚡ 데드락 위험 | XX | N | N | N | N |
| **종합** | **XX** | **N** | **N** | **N** | **N** |

## CRITICAL — 즉시 수정 필수
[CRITICAL 발견사항 — 데드락 즉각 재현 가능]

## HIGH — 머지 전 수정
[HIGH 발견사항]

## 락 정당화 주석 미비 목록
[주석 없거나 불충분한 락 위치 목록]
[각 항목에 필수 주석 템플릿 제공]

## Medium / Low
[요약]

## 총평 및 판정
APPROVE / REQUEST CHANGES / BLOCK
```

### Phase 6: 정리

1. TeamDelete
2. `_workspace/` 보존
3. 리포트 내용 출력 + 경로 안내

---

## 데이터 흐름

```
Phase 1: source.txt 수집
    ↓
Phase 2: TeamCreate (4명) + TaskCreate (4개, 의존성 설정)
    ↓
Phase 3: [lock-free-enforcer] ↔ [lock-justification-auditor] (병렬)
         락 목록 공유 via SendMessage
    ↓
Phase 4: [deadlock-analyzer] → [deadlock-reviewer] → (재분석 가능, 1회)
    ↓
Phase 5: 4개 JSON 통합 → concurrency_guard_report.md
    ↓
Phase 6: TeamDelete + 보고
```

---

## 에러 핸들링

| 상황 | 처리 |
|------|------|
| 에이전트 1명 실패 | SendMessage로 상태 확인 → 1회 재시작 → 재실패 시 해당 도메인 "수집 실패"로 표시 |
| lock-justification-auditor가 necessary_locks 미수신 | 소스 전체에서 직접 락 탐지로 전환 |
| deadlock-reviewer 재분석 요청 타임아웃 | 기존 분석 결과로 검증 진행 |
| 의견 불일치 3개+ | `disputed_findings`로 기록, 리더가 사용자에게 중재 요청 |

---

## 테스트 시나리오

### 정상 흐름
1. 사용자: "이 PR의 동시성 검사해줘 #23"
2. Phase 1: `gh pr diff 23` → source.txt
3. Phase 3: lock-free-enforcer가 `lock(_sync)` 3개 발견, 1개는 replaceable, 2개는 necessary. lock-justification-auditor에게 necessary_locks 전달.
4. lock-justification-auditor: 2개 necessary 락 중 1개는 [LOCK-REQUIRED] 없음 → HIGH 발견
5. Phase 4: deadlock-analyzer가 `.Result` 1개(CRITICAL), `ConfigureAwait` 누락 2개(MEDIUM) 발견
6. deadlock-reviewer: `.Result` 발견 확인(Confirmed), ConfigureAwait 중 1개는 Main에서만 호출 → 기각(Rejected)
7. Phase 5: 종합 리포트 생성, CRITICAL 1건 → BLOCK 판정

### 에러 흐름 (deadlock-reviewer 응답 없음)
1. Phase 4에서 deadlock-reviewer 타임아웃 (10분 초과)
2. 리더가 기존 `03_deadlock_analysis.json`으로 직접 Phase 5 진행
3. 리포트에 "⚠️ 데드락 검증 미완료 — 분석 결과 미검증" 명시
