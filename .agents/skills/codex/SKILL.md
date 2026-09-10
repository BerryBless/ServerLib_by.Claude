---
name: codex
description: >
  Codex 세션 안에서 OpenAI Codex CLI(`codex exec`)를 세컨드 오피니언·교차 검증·병렬 작업자로
  호출하는 스킬. 트리거: 'codex', '코덱스', 'codex에게 물어봐', 'codex 의견', '세컨드 오피니언',
  'codex 리뷰', 'codex로 검증', '교차 리뷰', 'codex한테 시켜'. 후속: 'codex 이어서', 'codex 다시'.
  기본은 read-only 샌드박스로 안전하게 실행하고, 파일 수정 위임은 사용자 확인 후 workspace-write로 실행한다.
---

# Codex 호출 스킬 (Codex ↔ Codex 협업)

## 전제

- `codex` CLI(v0.154+)가 전역 설치되어 있고 `~/.codex/auth.json`으로 ChatGPT 로그인 상태다. 확인: `codex login status`
- Codex는 이 리포의 `AGENTS.md`(프로젝트 규칙)와 `.agents/skills/`(스킬 미러)를 자동으로 읽는다.
  → 별도 컨텍스트 주입 없이 프로젝트 규칙이 공유된다.

## 호출 패턴

### 1) 세컨드 오피니언 / 질문 (기본, 읽기 전용)

```powershell
# -s read-only: 셸 명령이 읽기만 가능한 샌드박스 → 리포를 절대 변경하지 못함
# -o: 최종 응답을 파일로 받아 콘솔 인코딩(CP949) 깨짐 회피 → Read 툴로 UTF-8 읽기
codex exec -s read-only -o "$env:TEMP\codex_out.md" "질문 내용"
```

실행 후 `Read`로 `$env:TEMP\codex_out.md`를 읽어 사용자에게 요약 전달. **Codex 자신의 의견과 Codex 의견이 갈리는 지점을 명시**할 것.

### 2) 코드 리뷰 (Codex 내장 리뷰어)

```powershell
codex exec review          # 현재 리포 전체 대상
```

### 3) 편집 위임 (사용자가 명시적으로 요청한 경우만)

```powershell
# workspace-write: 리포 내부만 쓰기 허용. 프롬프트에 커밋 금지를 반드시 명시
codex exec -s workspace-write -o "$env:TEMP\codex_out.md" "작업 지시. 절대 git commit 하지 말 것."
```

완료 후 Codex가 `git diff`로 변경을 검토·요약하고 문제가 있으면 수정한다.

### 4) 직전 Codex 세션 이어가기

```powershell
codex exec resume --last -o "$env:TEMP\codex_out.md" "후속 질문"
```

## 실행 규칙

- **타임아웃:** 모델 호출로 수 분 걸릴 수 있다. Bash/PowerShell 툴 `timeout`을 300000~600000ms로 설정하고, 긴 작업은 `run_in_background`로 실행 후 알림을 기다린다.
- **커밋 금지:** 커밋은 Codex Stop 훅(`auto-commit.ps1`)의 책임이다. Codex에게 편집을 위임할 때 프롬프트에 "커밋하지 말 것"을 항상 포함하고, Codex가 `.git/auto_commit_msg.txt`를 만들지 않는지 확인한다 (AGENTS.md에도 금지 규칙 명시됨).
- **위험 플래그 금지:** `--dangerously-bypass-approvals-and-sandbox`는 사용하지 않는다. 쓰기가 필요하면 `workspace-write`까지만.
- **결과 귀속:** Codex의 판단을 인용할 때는 "Codex 의견"임을 밝히고, Codex가 검증한 결론과 구분해 보고한다.
