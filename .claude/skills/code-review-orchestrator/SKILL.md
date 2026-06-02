---
name: code-review-orchestrator
description: "종합 코드 리뷰 하네스를 실행하는 오케스트레이터. 아키텍처·보안·성능·코드 스타일을 4개 에이전트가 병렬 감사하고 단일 리포트로 통합한다. 트리거: '코드 리뷰', '리뷰 해줘', '코드 점검', 'PR 검토', '코드 감사', '종합 리뷰', '전체 리뷰', '이 코드 봐줘'. 후속 작업: '다시 리뷰', '리뷰 업데이트', '보안만 다시', '아키텍처 재검토', '이전 리뷰 개선', '리뷰 보완'. 파일 경로나 PR 번호가 언급되면 반드시 이 스킬을 사용할 것."
---

# Code Review Orchestrator

종합 코드 리뷰 팀을 조율하여 단일 리포트를 생성하는 오케스트레이터.

## 실행 모드: 에이전트 팀 (팬아웃/팬인)

## 에이전트 구성

| 팀원 | 에이전트 타입 | 역할 | 스킬 | 출력 |
|------|-------------|------|------|------|
| architecture-reviewer | architecture-reviewer | SOLID·레이어·결합도 감사 | /architecture-review | `_workspace/02_architecture_findings.json` |
| security-reviewer | security-reviewer | OWASP·CWE 기반 취약점 스캔 | /security-review | `_workspace/02_security_findings.json` |
| performance-reviewer | performance-reviewer | N+1·async·LINQ 병목 탐지 | /performance-review | `_workspace/02_performance_findings.json` |
| style-reviewer | style-reviewer | 네이밍·복잡도·문서화 감사 | /style-review | `_workspace/02_style_findings.json` |

---

## 워크플로우

### Phase 0: 컨텍스트 확인 (후속 작업 지원)

1. `_workspace/` 디렉토리 존재 여부 확인
2. 실행 모드 결정:
   - **`_workspace/` 미존재** → 초기 실행. Phase 1로 진행
   - **`_workspace/` 존재 + 특정 도메인 재검토 요청** (예: "보안만 다시") → **부분 재실행**:
     - 해당 에이전트만 재호출
     - 기존 다른 에이전트의 JSON은 그대로 유지
     - Phase 4(통합)만 다시 실행
   - **`_workspace/` 존재 + 새 코드/파일 제공** → **새 실행**:
     - 기존 `_workspace/`를 `_workspace_{YYYYMMDD_HHMMSS}/`로 이동
     - Phase 1부터 새로 시작

---

### Phase 1: 리뷰 대상 수집

사용자 입력에 따라 아래 중 하나를 실행하여 diff 내용을 수집한다.

**케이스 A — 인수 없음 (현재 브랜치 vs. main/master):**
```bash
# 베이스 브랜치 감지
BASE=$(git merge-base HEAD main 2>/dev/null || git merge-base HEAD master 2>/dev/null || git merge-base HEAD origin/main 2>/dev/null)
git diff $BASE HEAD
```

**케이스 B — 경로 지정 (예: `/comprehensive-review src/Api/`):**
```bash
# 특정 파일 또는 디렉토리의 현재 상태
cat <path>  # 단일 파일
# 또는 디렉토리 내 .cs 파일들을 순차 읽기
```

**케이스 C — PR 번호 (예: `/comprehensive-review 42`):**
```bash
gh pr diff 42
```

수집된 내용을 `_workspace/00_input/diff.txt`에 저장한다.
diff가 비어있으면 사용자에게 알리고 중지한다.

**diff 크기 관리:** diff가 800줄을 초과하면 파일별로 요약을 작성하여 컨텍스트 부담을 줄인다:
```
[파일 요약: src/Api/Controllers/UserController.cs — 150줄 추가, 주요 변경: 사용자 인증 엔드포인트 3개 추가]
```

---

### Phase 2: 팀 구성

```
TeamCreate(
  team_name: "code-review-team",
  members: [
    {
      name: "architecture-reviewer",
      agent_type: "architecture-reviewer",
      model: "opus",
      prompt: "당신은 아키텍처 리뷰어입니다. /architecture-review 스킬을 사용하여 _workspace/00_input/diff.txt를 감사하고 결과를 _workspace/02_architecture_findings.json에 저장하세요. 완료 후 리더에게 완료 메시지를 보내세요."
    },
    {
      name: "security-reviewer",
      agent_type: "security-reviewer",
      model: "opus",
      prompt: "당신은 보안 리뷰어입니다. /security-review 스킬을 사용하여 _workspace/00_input/diff.txt를 감사하고 결과를 _workspace/02_security_findings.json에 저장하세요. 완료 후 리더에게 완료 메시지를 보내세요."
    },
    {
      name: "performance-reviewer",
      agent_type: "performance-reviewer",
      model: "opus",
      prompt: "당신은 성능 리뷰어입니다. /performance-review 스킬을 사용하여 _workspace/00_input/diff.txt를 감사하고 결과를 _workspace/02_performance_findings.json에 저장하세요. 완료 후 리더에게 완료 메시지를 보내세요."
    },
    {
      name: "style-reviewer",
      agent_type: "style-reviewer",
      model: "opus",
      prompt: "당신은 스타일 리뷰어입니다. /style-review 스킬을 사용하여 _workspace/00_input/diff.txt를 감사하고 결과를 _workspace/02_style_findings.json에 저장하세요. 완료 후 리더에게 완료 메시지를 보내세요."
    }
  ]
)
```

작업 등록:
```
TaskCreate(tasks: [
  { title: "아키텍처 감사", description: "_workspace/00_input/diff.txt를 읽고 /architecture-review 스킬로 아키텍처 감사를 수행한다", assignee: "architecture-reviewer" },
  { title: "보안 취약점 스캔", description: "_workspace/00_input/diff.txt를 읽고 /security-review 스킬로 보안 취약점을 스캔한다", assignee: "security-reviewer" },
  { title: "성능 병목 탐지", description: "_workspace/00_input/diff.txt를 읽고 /performance-review 스킬로 성능 병목을 탐지한다", assignee: "performance-reviewer" },
  { title: "코드 스타일 감사", description: "_workspace/00_input/diff.txt를 읽고 /style-review 스킬로 스타일을 감사한다", assignee: "style-reviewer" }
])
```

---

### Phase 3: 병렬 감사 실행

**팀원들이 자체 조율하며 병렬 실행한다.**

리더는 팀원이 유휴 상태가 되면 자동 알림을 받는다. 알림을 기다리며:
- 특정 팀원이 막혔을 때 SendMessage로 지시한다
- 전체 진행률은 TaskGet으로 확인한다

**중복 발견 조율 규칙 (팀원에게 SendMessage로 전달):**
- 동일한 코드 위치에 대한 발견이 두 에이전트에서 나오면, 각자 독립적으로 기록한다 (관점이 다름)
- 단, 완전히 동일한 내용(같은 severity, 같은 제목)이면 더 관련성 높은 에이전트만 보고한다

모든 팀원의 태스크가 완료되면 Phase 4로 진행한다.

---

### Phase 4: 결과 통합 및 리포트 생성

1. 4개 JSON 파일을 Read로 수집한다:
   - `_workspace/02_architecture_findings.json`
   - `_workspace/02_security_findings.json`
   - `_workspace/02_performance_findings.json`
   - `_workspace/02_style_findings.json`

2. 파일이 없거나 파싱 실패 시: 해당 도메인을 "수집 실패"로 표시하고 나머지로 진행

3. **종합 점수 계산:**
   ```
   overall = security_score × 0.35
           + architecture_score × 0.25
           + performance_score × 0.25
           + style_score × 0.15
   ```

4. 다음 형식으로 리포트를 생성하여 `_workspace/03_consolidated_report.md`에 저장한다:

---

```markdown
# 종합 코드 리뷰 리포트
**생성:** {datetime}  |  **대상:** {target 설명}

---

## 종합 건강 점수

| 도메인 | 점수 | Critical | High | Medium | Low |
|--------|------|----------|------|--------|-----|
| 🏗️ 아키텍처 | XX / 100 | N | N | N | N |
| 🔒 보안 | XX / 100 | N | N | N | N |
| ⚡ 성능 | XX / 100 | N | N | N | N |
| 🎨 스타일 | XX / 100 | — | N | N | N |
| **종합** | **XX / 100** | **N** | **N** | **N** | **N** |

가중치: 보안 35% · 아키텍처 25% · 성능 25% · 스타일 15%

---

## Critical & High 발견사항 ← 머지 전 필수 수정

### [도메인] [SEVERITY] — 제목
**위치:** `파일명:라인`
**CWE:** CWE-XXX _(보안만)_
**문제:** 상세 설명
**수정:** 제안

_(없으면: "Critical/High 발견사항 없음 ✅")_

---

## Medium 발견사항 ← 권장 수정

_(없으면 생략)_

---

## Low / 정보성 ← 검토 권장

- [도메인] `파일명:라인` — 제목: 한줄 요약
- ...

_(없으면 생략)_

---

## 총평 및 판정

{3–5문장 종합 평가}

**판정: APPROVE / REQUEST CHANGES / BLOCK**

- **APPROVE**: Critical·High 없음, 전체 점수 80+
- **REQUEST CHANGES**: High 발견 또는 전체 점수 60–79
- **BLOCK**: Critical 발견 또는 전체 점수 60 미만
```

---

### Phase 5: 정리

1. 팀원들에게 종료 SendMessage: `{"action": "shutdown", "reason": "review-complete"}`
2. TeamDelete
3. `_workspace/` 보존 (중간 산출물 삭제 안 함 — 사후 확인용)
4. 사용자에게 리포트 내용 출력 + 파일 경로 안내:
   - 상세 리포트: `_workspace/03_consolidated_report.md`
   - 도메인별 원본: `_workspace/02_{domain}_findings.json`

---

## 데이터 흐름

```
사용자 요청
    │
    ▼
Phase 1: diff 수집 → _workspace/00_input/diff.txt
    │
    ▼
Phase 2: TeamCreate (4명) + TaskCreate (4개)
    │
    ▼
Phase 3: 병렬 감사 (팀원 자체 조율)
    ├── architecture-reviewer → 02_architecture_findings.json
    ├── security-reviewer     → 02_security_findings.json
    ├── performance-reviewer  → 02_performance_findings.json
    └── style-reviewer        → 02_style_findings.json
    │
    ▼
Phase 4: 4개 JSON 통합 → 03_consolidated_report.md
    │
    ▼
Phase 5: TeamDelete + 사용자 보고
```

---

## 에러 핸들링

| 상황 | 처리 |
|------|------|
| 팀원 1명 실패 | SendMessage로 상태 확인 → 1회 재시작 시도 → 재실패 시 해당 도메인 "수집 실패"로 표시하고 계속 |
| 팀원 2명+ 실패 | 사용자에게 알리고 진행 여부 확인 |
| JSON 파싱 실패 | 해당 도메인 건너뜀, 리포트에 "파싱 실패" 명시 |
| diff 없음 | 즉시 중지, 사용자에게 대상 지정 요청 |
| 타임아웃 (팀원 응답 없음 10분+) | 현재까지 수집된 결과로 Phase 4 진행, 미완료 팀원은 종료 |

---

## 테스트 시나리오

### 정상 흐름
1. 사용자: "이 PR 리뷰해줘 #15"
2. Phase 1: `gh pr diff 15` 실행 → diff 수집
3. Phase 2: 4명 팀 생성, 4개 태스크 등록
4. Phase 3: 4명이 병렬로 각자 감사 수행, 중복 발견 조율
5. Phase 4: JSON 4개 통합, 종합 점수 산출, 리포트 생성
6. Phase 5: 팀 정리, 리포트 출력
7. 예상: `_workspace/03_consolidated_report.md` 생성, 판정 제시

### 에러 흐름 (팀원 1명 실패)
1. Phase 3 중 performance-reviewer가 에러로 중지
2. 리더가 유휴 알림 수신
3. SendMessage로 상태 확인 → 재시작 시도
4. 재시작 실패 시 나머지 3개 도메인 결과로 Phase 4 진행
5. 리포트에 "⚠️ performance 도메인 수집 실패 — 수동 확인 필요" 명시
