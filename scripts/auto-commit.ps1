# auto-commit.ps1
# Stop 훅에서 호출 — claude -p 로 WHY 중심 커밋 메시지 생성 후 자동 커밋 & 푸시

# [필수] 외부 프로세스(claude.exe) stdout을 UTF-8로 디코딩.
# 한국어 Windows 기본 콘솔 인코딩은 CP949(ks_c_5601-1987)라서
# UTF-8로 출력되는 claude 결과의 한글이 깨지고, 검증 정규식(^수정|^추가...)이
# 첫 글자부터 실패하여 폴백 메시지로 커밋되는 문제를 방지한다.
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

# 5. diff 수집 (8000자 제한)
$diff = (git -C $repo diff --staged 2>&1) -join "`n"
if ($diff.Length -gt 8000) { $diff = $diff.Substring(0, 8000) + "`n...(truncated)" }

# 6. claude -p 로 WHY 중심 커밋 메시지 생성
#    - --model haiku: 커밋 메시지 생성에 Opus는 불필요하고 느림. Haiku로 충분하며 빠름.
#    - Start-Job 35초 가드: claude -p 가 콜드스타트(플러그인·MCP 로드)로 60초를 넘기면
#      훅이 add 후 commit 전에 kill 되어 staged 상태로 남는 문제를 방지한다.
#      35초 초과 시 Stop-Job → 빈 문자열 반환 → 아래 폴백 로직이 접두사 기반 메시지를 생성하고
#      git commit 은 반드시 실행된다.
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

$claudePath = "C:\Users\aaa\.local\bin\claude.exe"
$job = Start-Job -ScriptBlock {
    param($p, $cp)
    # 자식 런스페이스에서도 UTF-8 출력 디코딩 (c52cd95 인코딩 버그 재발 방지)
    try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
    (& $cp -p $p --model haiku 2>$null) -join "`n"
} -ArgumentList $prompt, $claudePath

$done = Wait-Job $job -Timeout 35
if ($done) {
    $commitMsg = (Receive-Job $job).Trim()
} else {
    Stop-Job $job
    $commitMsg = ""
}
Remove-Job $job -Force 2>$null

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
