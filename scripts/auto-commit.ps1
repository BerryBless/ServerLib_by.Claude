# auto-commit.ps1
# Stop 훅에서 호출 — claude -p 로 WHY 중심 커밋 메시지 생성 후 자동 커밋 & 푸시

$repo = "E:\project\ClaudeCodeStudy"

# 1. 변경사항 확인
$status = git -C $repo status --porcelain 2>&1
if (-not $status) { exit 0 }

# 2. 보안 검사 — 민감 파일 감지 시 차단
$statusStr = ($status | Where-Object { $_ }) -join ' '
$sensitiveHit = ($statusStr -match '(?i)\.(env|pem|key|p12|pfx|jks|ppk)(\s|$)|id_rsa|id_ed25519|credentials\.json|secrets\.json') `
                -and ($statusStr -notmatch '\.example')
if ($sensitiveHit) {
    Write-Output '{"systemMessage":"⚠️ 민감 파일 감지 — 자동 커밋 차단. /commitandpush 로 수동 처리 필요."}'
    exit 2
}

# 3. 전체 스테이지
git -C $repo add . 2>&1 | Out-Null

# 4. 스테이지된 파일 확인
$staged = @(git -C $repo diff --staged --name-only 2>&1 | Where-Object { $_ -ne '' })
if ($staged.Count -eq 0) { exit 0 }

# 5. diff 수집 (8000자 제한)
$diff = (git -C $repo diff --staged 2>&1) -join "`n"
if ($diff.Length -gt 8000) { $diff = $diff.Substring(0, 8000) + "`n...(truncated)" }

# 6. claude -p 로 WHY 중심 커밋 메시지 생성
$prompt = @"
아래 git diff를 분석하여 한국어 커밋 메시지를 작성하라.

[규칙]
- 형식: {접두사}: {제목}
- 유효 접두사: 추가, 수정, 버그수정, 리팩토링, 문서, 테스트, 의존성
- 금지 접두사: 자동, update, fix, add, chore
- 제목: 50자 이내, 파일명 나열 금지, WHY(왜) 중심으로 구체적으로 작성
  - 나쁜 예: "수정: RudpChannel·SpanWriter 변경"
  - 좋은 예: "수정: ArrayPool 반환 누락으로 인한 RUDP 메모리 누수 수정"
- 본문: 변경이 단순하면 생략, 복잡하면 '- '로 시작하는 항목 나열
- 마지막 줄(필수): Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
- 커밋 메시지 텍스트만 출력할 것, 다른 설명 추가 금지

[diff]
$diff
"@

$commitMsg = (claude -p $prompt 2>&1) -join "`n"
$commitMsg = $commitMsg.Trim()

# 생성 실패 시 접두사 기반 폴백
if (-not $commitMsg -or $commitMsg -notmatch '^(추가|수정|버그수정|리팩토링|문서|테스트|의존성):') {
    $added = @(git -C $repo diff --staged --name-only --diff-filter=A 2>&1 | Where-Object { $_ })
    $prefix = if ($added.Count -eq $staged.Count) { "추가" } else { "수정" }
    $first  = [System.IO.Path]::GetFileNameWithoutExtension($staged[0])
    $commitMsg = "${prefix}: $first 외 $($staged.Count - 1)개 파일 변경`n`nCo-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"
}

# 7. 커밋
git -C $repo commit -m $commitMsg 2>&1

# 8. 원격 저장소가 있으면 푸시
if ($LASTEXITCODE -eq 0) {
    $remote = git -C $repo remote 2>&1
    if ($remote) { git -C $repo push 2>&1 | Out-Null }
}
