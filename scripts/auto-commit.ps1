# auto-commit.ps1
# Stop 훅에서 호출 — 메인 세션이 .git/auto_commit_msg.txt 에 남긴 WHY 커밋 메시지를
# 읽어서 커밋 & 푸시한다. 파일이 없거나 형식이 맞지 않으면 접두사 기반 폴백 메시지 사용.
# (이전 방식: Stop 훅이 claude -p 를 직접 호출 → 콜드스타트/stdin 취약성으로 폴백 빈발)

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

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

# 5. 메인 세션이 남긴 WHY 메시지 파일 읽기 (consume-once)
#    메인 Claude 가 턴 종료 직전에 .git/auto_commit_msg.txt 를 작성해두면
#    훅이 그 메시지를 사용하고 파일을 삭제한다. nested claude 세션 없이 즉시 완료.
$msgFile = Join-Path $repo ".git\auto_commit_msg.txt"
$commitMsg = ""
if (Test-Path $msgFile) {
    $commitMsg = (Get-Content $msgFile -Raw -Encoding UTF8).Trim()
    Remove-Item $msgFile -Force 2>$null   # 재사용 방지: 읽는 즉시 삭제
}

# 6. 메시지 없거나 형식 불량 시 접두사 기반 폴백
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
