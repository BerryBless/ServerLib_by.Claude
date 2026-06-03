# auto-commit.ps1
# Stop 훅에서 호출 — commit-message-guide.md 규칙으로 자동 커밋 & 푸시

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

# 4. 스테이지된 파일 목록
$staged = @(git -C $repo diff --staged --name-only 2>&1 | Where-Object { $_ -ne '' })
if ($staged.Count -eq 0) { exit 0 }

# 5. 변경 유형별 분류
$added   = @(git -C $repo diff --staged --name-only --diff-filter=A 2>&1 | Where-Object { $_ })
$deleted = @(git -C $repo diff --staged --name-only --diff-filter=D 2>&1 | Where-Object { $_ })

# 6. 접두사 판단 (commit-message-guide.md 판단 트리)
$testCount   = ($staged | Where-Object { $_ -match '(?i)test|spec|Test' }).Count
$docCount    = ($staged | Where-Object { $_ -match '(?i)\.md$|readme' }).Count
$csprojCount = ($staged | Where-Object { $_ -match '\.(csproj|sln)$' }).Count
$onlyAdded   = ($added.Count -gt 0) -and ($deleted.Count -eq 0) -and ($staged.Count -eq $added.Count)

$prefix = if     ($testCount   -eq $staged.Count) { "테스트" }
          elseif ($docCount    -eq $staged.Count) { "문서"   }
          elseif ($csprojCount -eq $staged.Count) { "의존성" }
          elseif ($onlyAdded)                      { "추가"   }
          else                                     { "수정"   }

# 7. 제목 생성 — 파일명 기반, 의미 있는 동사 포함
$names = $staged `
    | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) } `
    | Select-Object -Unique
$n = $names.Count

$verb = if ($prefix -eq "추가") { "추가" } elseif ($prefix -eq "테스트") { "작성" } else { "변경" }

$title = if ($n -eq 1) {
    "$($names[0]) $verb"
} elseif ($n -le 3) {
    ($names -join '·') + " $verb"
} else {
    "$($names[0]) 외 $($n - 1)개 $verb"
}

# 8. 커밋 메시지 조합 (commit-msg 훅 통과 형식)
$msg = "${prefix}: ${title}`n`nCo-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"
git -C $repo commit -m $msg 2>&1

# 9. 원격 저장소가 있으면 푸시
if ($LASTEXITCODE -eq 0) {
    $remote = git -C $repo remote 2>&1
    if ($remote) { git -C $repo push 2>&1 | Out-Null }
}
