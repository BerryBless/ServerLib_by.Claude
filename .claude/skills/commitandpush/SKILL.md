---
name: commitandpush
description: >
  Git 변경사항을 보안 검증 → 한국어 커밋 메시지 자동 생성 → 안전한 커밋 및 푸시까지 처리하는 자동화 파이프라인.
  '/commitandpush', '커밋해줘', '커밋하고 푸시해줘', '변경사항 올려줘', '깃 커밋', '깃 푸시', '자동 커밋',
  '커밋 메시지 만들어서 올려줘', 'git commit and push', '커밋 자동화' 요청 시 반드시 이 스킬을 사용할 것.
  후속 실행: '다시 커밋해줘', '커밋 재실행', '이전 커밋 수정', '보안 재검사 후 커밋' 포함.
  민감 정보(.env, 하드코딩 키, API 토큰 등) 감지 시 커밋을 원천 차단한다.
---

# commitandpush 오케스트레이터

## 실행 모드: 서브 에이전트 파이프라인 (순차 실행)

```
git-security-auditor → git-commit-writer → git-push-controller
     (PASS/FAIL)           (메시지 생성)        (커밋 & 푸시)
```

---

## 절대 금지 규칙 (오케스트레이터 자체도 준수)

이 파이프라인 내 모든 에이전트는 아래 명령을 **어떤 이유로도** 실행할 수 없다:

| 금지 명령 | 사유 |
|----------|------|
| `git config *` | 시스템 설정 불변 |
| `git reset --hard` | 사용자 미동의 작업 손실 |
| `git clean -fd` | 추적 불가 파일 영구 삭제 |
| `git push --force`, `git push -f` | 원격 히스토리 강제 덮어쓰기 |
| `git rebase -i`, `git add -i` 등 `-i` | 인터랙티브 명령 지원 불가 |
| `git commit --amend` | pre-commit hook 조건부 예외 외 금지 |

---

## Phase 0: 컨텍스트 확인

1. `_workspace/` 존재 여부 확인:
   - **미존재** → 초기 실행. `_workspace/` 생성 후 Phase 1 진행
   - **존재 + 사용자가 재실행 요청** → 기존 결과 파일 확인 후 단계 결정:
     - 보안 재검사 요청 → Phase 1부터 재실행
     - 커밋 메시지만 재작성 → Phase 2부터 재실행
     - 기존 메시지로 재커밋 → Phase 3부터 재실행
   - **존재 + 새 실행** → `_workspace/`를 `_workspace_{YYYYMMDD_HHMMSS}/`로 이동 후 재생성

2. `git status --porcelain` 실행:
   - 변경사항 없으면 → "커밋할 변경사항이 없습니다" 안내 후 종료
   - 스테이지된 파일 없으면 → 사용자에게 전체 스테이지 여부 확인:
     ```
     스테이지된 파일이 없습니다. 모든 변경사항을 스테이지하겠습니까?
     (y/n) — 기본값: y
     ```
     - y: `git add .` 실행 후 계속
     - n: 사용자에게 직접 스테이지 후 재실행 안내

---

## Phase 1: 보안 감사 (git-security-auditor)

```python
agent = Agent(
    agent_definition="git-security-auditor",
    subagent_type="general-purpose",
    model="opus",
    prompt="""
    git-security-auditor.md 에이전트 역할을 수행하라.
    
    1. git status --porcelain 실행
    2. git diff --staged 전체 스캔
    3. git diff 전체 스캔 (unstaged 포함)
    4. references/security-patterns.md 의 패턴으로 민감 정보 탐지
    5. 결과를 _workspace/01_security_result.md 에 저장
    
    작업 디렉토리: {project_root}
    """
)
```

**판정에 따른 분기:**
- **PASS** → Phase 2 진행
- **FAIL (CRITICAL/HIGH)** → 파이프라인 즉시 중단
  - 발견된 민감 정보 파일/라인 사용자에게 명시
  - 수정 방법 안내 (예: `.gitignore` 추가, 값 환경 변수화)
  - 수정 후 `/commitandpush` 재실행 안내
- **WARN (MEDIUM)** → 사용자 확인 요청 후 계속 여부 결정

---

## Phase 2: 커밋 메시지 작성 (git-commit-writer)

```python
agent = Agent(
    agent_definition="git-commit-writer",
    subagent_type="general-purpose",
    model="opus",
    prompt="""
    git-commit-writer.md 에이전트 역할을 수행하라.
    
    1. git log --oneline -10 실행하여 스타일 학습
    2. git diff --staged --stat 으로 변경 파일 목록 확인
    3. git diff --staged 로 변경 내용 분석
    4. references/commit-message-guide.md 의 규칙에 따라 한국어 커밋 메시지 작성
       ★ 반드시 접두사 판단 트리를 사용: 추가/수정/버그수정/리팩토링/문서/테스트/의존성
       ★ '자동:', 'update:', 'fix:', 'add:' 등 금지 접두사 절대 사용 금지
       ★ 파일명 나열 형식('ServerLib/Core/... 외 N개 수정') 금지 — WHY 중심 메시지 작성
    5. 메시지를 _workspace/02_commit_message.txt 에 저장
    
    작업 디렉토리: {project_root}
    보안 결과: _workspace/01_security_result.md (PASS 확인)
    """
)
```

생성된 메시지를 사용자에게 미리보기로 출력:
```
📝 생성된 커밋 메시지:
─────────────────────────────
수정: SocketPipelineSession 패킷 프레이밍 개선

- TryReadPacket 헬퍼로 완전한 패킷 경계 감지
- AdvanceTo consumed/examined 분리 처리

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
─────────────────────────────
이 메시지로 커밋하시겠습니까? (y/n/edit)
```

- **y**: Phase 3 진행
- **n**: 파이프라인 중단
- **edit**: 사용자가 직접 메시지 입력 후 Phase 3 진행

---

## Phase 3: 커밋 & 푸시 (git-push-controller)

```python
agent = Agent(
    agent_definition="git-push-controller",
    subagent_type="general-purpose",
    model="opus",
    prompt="""
    git-push-controller.md 에이전트 역할을 수행하라.
    
    1. _workspace/02_commit_message.txt 에서 커밋 메시지 읽기
    2. git remote -v 로 원격 저장소 확인
    3. 현재 브랜치 확인 및 보호 브랜치 여부 체크
    4. git commit -m "$(cat _workspace/02_commit_message.txt)" 실행
    5. pre-commit hook 실패 시 조건부 amend 처리
    6. git push (원격 있으면), 원격 없으면 로컬 커밋만
    7. 결과를 _workspace/03_push_result.md 에 저장
    
    작업 디렉토리: {project_root}
    절대 금지: force push, reset --hard, clean -fd, git config 변경, -i 명령
    """
)
```

---

## Phase 4: 결과 보고

`_workspace/03_push_result.md`를 읽어 사용자에게 최종 요약 출력:

```
✅ 커밋 & 푸시 완료
─────────────────────────────
커밋: abc1234 (수정: SocketPipelineSession 패킷 프레이밍 개선)
브랜치: feature/packet-serialization
원격: origin ✓
─────────────────────────────
```

또는 실패 시:
```
❌ 파이프라인 중단: [단계] — [사유]
─────────────────────────────
[구체적 조치 안내]
─────────────────────────────
```

---

## 에러 핸들링

| 상황 | 처리 |
|------|------|
| git 저장소 아님 | 즉시 중단, "git init 또는 올바른 디렉토리에서 실행" 안내 |
| 보안 감사 에이전트 실패 | FAIL로 처리, 안전 원칙에 따라 중단 |
| 커밋 메시지 생성 실패 | 사용자에게 수동 입력 요청 |
| push 인증 실패 | 오류 메시지 그대로 전달, 자격증명 안내 |
| 네트워크 오류 | 로컬 커밋은 유지, push 재시도 안내 |
| pre-commit hook 파이프라인 중단 | hook 오류 원문 출력, 수동 처리 안내 |

---

## 테스트 시나리오

### 정상 흐름
1. 사용자: `/commitandpush`
2. Phase 0: 변경사항 있음, 전체 스테이지 확인
3. Phase 1: 보안 스캔 PASS
4. Phase 2: "수정: 패킷 직렬화 개선" 메시지 생성 → 사용자 y 확인
5. Phase 3: 커밋 성공, origin push 성공
6. 결과: ✅ 커밋 & 푸시 완료

### 보안 차단 흐름
1. 사용자: `/commitandpush`
2. Phase 1: `.env` 파일 diff 발견 → FAIL
3. 출력: "❌ 민감 정보 발견: .env (CRITICAL)\n.gitignore에 추가 후 재실행하세요"
4. 파이프라인 중단

### 원격 없는 첫 커밋
1. Phase 0: 스테이지 없음 → git add . 실행
2. Phase 3: `git remote -v` 결과 없음 → 로컬 커밋만 수행
3. 출력: "✅ 로컬 커밋 완료 (원격 저장소 없음)\n원격 추가: git remote add origin {URL}"
