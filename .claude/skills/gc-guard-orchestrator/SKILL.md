---
name: gc-guard-orchestrator
description: ".NET 10 서버 라이브러리의 GC 압력 억제를 위한 메모리 최적화 팀을 조율하는 오케스트레이터. 힙 할당 스캐너와 풀링 강제자가 병렬 감사 후 상호 리뷰를 거쳐 단일 GC 가드 리포트를 생성한다. 트리거: 'GC 억제', '힙 할당 감사', '메모리 최적화', 'ArrayPool 검사', 'ValueTask 검증', 'Span 적용', 'boxing 탐지', 'GC 압력 분석', '메모리 최적화 리뷰'. 후속 작업: '다시 분석', 'GC 재검토', '할당 보완', '이전 결과 업데이트', 'ValueTask만 다시'."
---

# GC Guard Orchestrator

.NET 10 고성능 서버 라이브러리의 GC 억제를 위한 메모리 최적화 팀을 조율하는 오케스트레이터.

## 실행 모드: 하이브리드 에이전트 팀

| Phase | 모드 | 팀 구성 | 이유 |
|-------|------|---------|------|
| Phase A (병렬 감사) | 에이전트 팀 | heap-allocation-scanner ↔ pooling-enforcer | 버퍼 할당 발견을 실시간 공유, 상호 보완 |
| Phase B (교차 검증) | 에이전트 팀 (동일) | allocation-peer-reviewer | 두 보고서를 독립 교차 검증 |

4개 에이전트 대신 **3개 에이전트**를 단일 팀으로 구성하고, `depends_on`으로 Phase A/B 순서를 모델링한다.

## 에이전트 구성

| 팀원 | 에이전트 타입 | 스킬 | 출력 |
|------|-------------|------|------|
| heap-allocation-scanner | heap-allocation-scanner | /heap-allocation-scan | `02_allocation_findings.json` |
| pooling-enforcer | pooling-enforcer | /pooling-enforcement | `02_pooling_findings.json` |
| allocation-peer-reviewer | allocation-peer-reviewer | /allocation-peer-review | `03_peer_review.json` |

---

## 워크플로우

### Phase 0: 컨텍스트 확인

1. `_workspace/` 존재 여부 확인
2. 분기:
   - **미존재** → 초기 실행. Phase 1 진행
   - **존재 + 특정 에이전트 재실행** ("ValueTask만 다시") → 부분 재실행: 해당 에이전트만 재호출 후 Phase 4(리포트) 재통합
   - **존재 + 새 코드** → 새 실행: 기존 `_workspace/`를 `_workspace_{YYYYMMDD_HHMMSS}/`로 이동 후 Phase 1

### Phase 1: 분석 대상 수집

**케이스 A — 현재 브랜치 diff:**
```bash
BASE=$(git merge-base HEAD main 2>/dev/null || git merge-base HEAD master 2>/dev/null)
git diff $BASE HEAD -- "*.cs"
```

**케이스 B — 경로 지정:**
```bash
find <path> -name "*.cs" | xargs cat
```

**케이스 C — PR 번호:**
```bash
gh pr diff <PR번호> -- "*.cs"
```

수집 내용을 `_workspace/00_input/source.txt`에 저장한다.
빈 경우 사용자에게 대상 지정 요청 후 중지한다.

### Phase 2: 팀 구성

```
TeamCreate(
  team_name: "gc-guard-team",
  members: [
    {
      name: "heap-allocation-scanner",
      agent_type: "heap-allocation-scanner",
      model: "opus",
      prompt: "당신은 heap-allocation-scanner입니다. /heap-allocation-scan 스킬을 사용하여 _workspace/00_input/source.txt의 hot path 힙 할당을 탐지하고 _workspace/02_allocation_findings.json에 저장하세요. 버퍼/배열 관련 발견은 즉시 pooling-enforcer에게 SendMessage로 공유하세요."
    },
    {
      name: "pooling-enforcer",
      agent_type: "pooling-enforcer",
      model: "opus",
      prompt: "당신은 pooling-enforcer입니다. /pooling-enforcement 스킬을 사용하여 _workspace/00_input/source.txt의 ValueTask/Span/ArrayPool 패턴을 점검하고 _workspace/02_pooling_findings.json에 저장하세요. heap-allocation-scanner로부터 버퍼 할당 공유를 수신하면 해당 위치를 우선 분석하세요."
    },
    {
      name: "allocation-peer-reviewer",
      agent_type: "allocation-peer-reviewer",
      model: "opus",
      prompt: "당신은 allocation-peer-reviewer입니다. heap-allocation-scanner와 pooling-enforcer 모두 완료된 후 /allocation-peer-review 스킬을 사용하여 두 보고서를 독립 교차 검증하고 _workspace/03_peer_review.json에 저장하세요."
    }
  ]
)
```

작업 등록:
```
TaskCreate(tasks: [
  {
    title: "힙 할당 스캔",
    description: "/heap-allocation-scan 스킬로 hot path 할당 탐지",
    assignee: "heap-allocation-scanner"
  },
  {
    title: "풀링 패턴 강제",
    description: "/pooling-enforcement 스킬로 ValueTask/Span/ArrayPool 점검",
    assignee: "pooling-enforcer"
  },
  {
    title: "교차 검증",
    description: "/allocation-peer-review 스킬로 두 보고서 독립 검증",
    assignee: "allocation-peer-reviewer",
    depends_on: ["힙 할당 스캔", "풀링 패턴 강제"]
  }
])
```

### Phase 3: Phase A — 병렬 감사

**실행 모드: 에이전트 팀 (팬아웃)**

heap-allocation-scanner와 pooling-enforcer가 병렬 실행하며 SendMessage로 발견 공유.

**통신 흐름:**
```
heap-allocation-scanner  →  [버퍼 할당 목록]  →  pooling-enforcer
         ↓                                               ↓
02_allocation_findings.json                02_pooling_findings.json
         ↓                                               ↓
                    리더에게 각자 완료 알림
```

두 에이전트가 모두 완료 알림을 보내면 Phase B로 진행한다.

### Phase 4: Phase B — 교차 검증

allocation-peer-reviewer가 두 보고서를 독립 검증한다.

```
02_allocation_findings.json ─┐
                              ├─▶ allocation-peer-reviewer ─▶ 03_peer_review.json
02_pooling_findings.json ────┘           +
source.txt (독립 FN 탐지)
```

### Phase 5: 결과 통합 및 리포트 생성

3개 파일을 Read로 수집:
- `_workspace/02_allocation_findings.json`
- `_workspace/02_pooling_findings.json`
- `_workspace/03_peer_review.json`

**종합 점수:**
```
final_score = peer_review.final_score  (교차 검증 후 확정 점수)
allocation_raw = allocation_findings.score  (스캐너 원점수, 참고용)
pooling_raw = pooling_findings.score    (강제자 원점수, 참고용)
```

**리포트 형식** (`_workspace/04_gc_guard_report.md`):

```markdown
# GC 가드 리포트
생성: {datetime}  |  대상: {target}

## 메모리 건강 점수
| 에이전트 | 원점수 | 확정 점수 |
|---------|--------|----------|
| 힙 할당 스캐너 | XX | (교차 검증 후) |
| 풀링 강제자 | XX | (교차 검증 후) |
| **최종 (교차 검증)** | — | **XX / 100** |

## CRITICAL — 즉시 수정 (GC 압력 심각)
[Confirmed critical 발견]
위치 / 패턴 / 문제 / 수정 코드

## HIGH — 머지 전 수정
[Confirmed high 발견 + FN에서 추가된 발견]

## ArrayPool.Return 누락 목록
[모든 Rent-without-Return 발견, CRITICAL 수준]

## 교차 검증 결과
- 기각된 False Positive: N건
- 추가 발견(FN): N건
- 수정된 fix_code: N건

## Medium / Low
[요약 목록]

## 판정
APPROVE / REQUEST CHANGES / BLOCK
```

### Phase 6: 정리

1. TeamDelete
2. `_workspace/` 보존
3. 리포트 출력 + 경로 안내

---

## 에러 핸들링

| 상황 | 처리 |
|------|------|
| 에이전트 1명 실패 | 1회 재시작 → 재실패 시 해당 도메인 누락 표시 |
| 두 에이전트 모두 실패 | 사용자에게 알리고 진행 여부 확인 |
| 교차 검증 FP 비율 60%+ | 리포트에 "스캐너 방법론 재검토 권고" 명시 |
| peer_review.json 없음 | 두 원본 보고서로 리포트 생성, 검증 미완료 표시 |

---

## 테스트 시나리오

### 정상 흐름
1. 사용자: "Server.cs 메모리 최적화 검사해줘"
2. Phase 1: Server.cs 읽어 source.txt 저장
3. Phase 3: scanner가 `new byte[1024]` in loop(HIGH), boxer가 `Task<T>` → `ValueTask<T>` 교체 제안
4. scanner가 `new byte[1024]` 발견을 pooling-enforcer에게 SendMessage
5. pooling-enforcer가 ArrayPool 대안 코드 스니펫 작성
6. Phase 4: peer-reviewer가 scanner의 클로저 발견 1건 FP 기각(static lambda), FN 1건 추가(params boxing)
7. Phase 5: 최종 점수 72, REQUEST CHANGES

### 에러 흐름 (pooling-enforcer 실패)
1. Phase 3에서 pooling-enforcer 응답 없음 (10분 초과)
2. allocation-peer-reviewer가 allocation_findings.json만으로 검증 진행
3. 리포트에 "⚠️ 풀링 강제 에이전트 미완료 — ValueTask/ArrayPool 검증 수동 필요" 명시
