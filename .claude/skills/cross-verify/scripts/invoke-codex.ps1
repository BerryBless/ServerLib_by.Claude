# invoke-codex.ps1 — Codex CLI 비대화형 실호출 래퍼 (cross-verify 하네스 전용)
#
# 목적: 실제 Codex CLI(codex exec)를 호출하고, "실제로 실행되었다"는 증빙(meta JSON)을 남긴다.
#       exit code·소요 시간·출력 크기를 기록하므로, Claude 에이전트가 Codex 출력을 위조하면
#       meta 파일 부재/불일치로 즉시 탐지된다.
#
# 사용:
#   pwsh -File invoke-codex.ps1 -PromptFile <프롬프트.md> -OutFile <응답저장.md> [-TimeoutSec 600] [-Cd <작업루트>]
#
# 결과:
#   - OutFile        : Codex의 최종 응답 (codex exec -o)
#   - OutFile+.meta.json : { status, exit_code, duration_sec, out_bytes, started_at, cmd }
#     status = "success" | "timeout" | "error" | "empty-output" | "quota"
#     quota  = 사용량/토큰 한도 실패 — 오케스트레이터가 Claude 단독 폴백(저하 모드)으로 전환하는 트리거
#   - exit code      : success=0, 그 외=1  (호출 실패는 검증 통과와 반드시 구분할 것)
#
# 안전 규칙:
#   - 샌드박스는 read-only 고정 — Codex는 검증 역할이므로 프로젝트 코드를 절대 수정하지 않는다.
#   - 프롬프트는 stdin으로 전달(-) — 커맨드라인 길이 제한·이스케이프 문제를 회피한다.

param(
    [Parameter(Mandatory = $true)][string]$PromptFile,
    [Parameter(Mandatory = $true)][string]$OutFile,
    [int]$TimeoutSec = 600,
    [string]$Cd = "E:\project\ClaudeCodeStudy"
)

$ErrorActionPreference = 'Stop'
$metaFile = "$OutFile.meta.json"
$logFile  = "$OutFile.log"       # codex 진행 이벤트(stdout) — 디버깅용
$errFile  = "$OutFile.err"       # stderr — 인증 오류 등 원인 판별용
$startedAt = Get-Date

function Write-Meta([string]$status, [int]$exitCode) {
    $outBytes = (Test-Path $OutFile) ? (Get-Item $OutFile).Length : 0
    [ordered]@{
        status       = $status
        exit_code    = $exitCode
        duration_sec = [math]::Round(((Get-Date) - $startedAt).TotalSeconds, 1)
        out_file     = $OutFile
        out_bytes    = $outBytes
        started_at   = $startedAt.ToString('o')
        prompt_file  = $PromptFile
        sandbox      = 'read-only'
    } | ConvertTo-Json | Set-Content -Path $metaFile -Encoding utf8
}

# 사용량/토큰 한도 실패 판별 — err 파일 전체를 검사한다. 예외·비정상종료·빈출력 등 모든 실패 경로에서
# 동일 기준으로 분류해, 오케스트레이터가 Claude 단독 폴백을 일관되게 판단하도록 한다.
# 'token' 단독 매칭은 인증 토큰 오류와 혼동되므로 제외하고 한도 관련 표현만 매칭한다.
function Test-QuotaError {
    if (-not (Test-Path $errFile)) { return $false }
    $t = Get-Content $errFile -Raw
    return [bool]($t -match "(?i)rate[ _-]?limit|usage[ _-]?limit|hit your (usage|limit)|too many requests|quota|\b429\b|insufficient[ _-]?(credits|quota)|usage cap|limit reached|purchase more credits")
}

if (-not (Test-Path $PromptFile)) { Write-Meta 'error' -1; Write-Error "프롬프트 파일 없음: $PromptFile"; exit 1 }

# codex.cmd: npm 전역 셰이 — CreateProcess가 .cmd를 %COMSPEC% 경유로 실행하므로 리다이렉트와 함께 직접 기동 가능
$codex = (Get-Command codex.cmd -ErrorAction SilentlyContinue) ?? (Get-Command codex -CommandType Application -ErrorAction SilentlyContinue)
if (-not $codex) { Write-Meta 'error' -1; Write-Error "codex CLI를 PATH에서 찾을 수 없음 — npm install -g @openai/codex"; exit 1 }

# 이전 출력 잔재 제거: 오래된 응답이 이번 호출 결과로 오인되는 것을 방지
Remove-Item -Path $OutFile, $metaFile -ErrorAction SilentlyContinue

$args = @(
    'exec',
    '-s', 'read-only',        # 검증 역할 — 쓰기 금지 샌드박스 고정
    '-C', $Cd,                 # AGENTS.md·코드를 읽을 프로젝트 루트
    '--color', 'never',        # 로그 파일에 ANSI 코드 미포함
    '-o', $OutFile,            # 최종 응답만 파일로 분리 수집
    '-'                        # 프롬프트를 stdin에서 읽음
)

try {
    $proc = Start-Process -FilePath $codex.Source -ArgumentList $args `
        -RedirectStandardInput $PromptFile `
        -RedirectStandardOutput $logFile `
        -RedirectStandardError $errFile `
        -NoNewWindow -PassThru

    if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
        $proc.Kill($true)   # 자식 프로세스 트리까지 종료
        Write-Meta 'timeout' -1
        Write-Error "Codex 호출 타임아웃 (${TimeoutSec}s) — 검증 통과 아님"
        exit 1
    }

    $isQuota = Test-QuotaError   # 한도 실패는 exit code가 정상이든 아니든 err 텍스트로 판별

    if ($proc.ExitCode -ne 0) {
        Write-Meta ($isQuota ? 'quota' : 'error') $proc.ExitCode
        $errTail = (Test-Path $errFile) ? (((Get-Content $errFile -Raw) -split "`n" | Select-Object -Last 5) -join ' | ') : ''
        Write-Error "Codex 호출 실패 ($($isQuota ? '토큰/사용량 한도' : 'error'), exit $($proc.ExitCode)): $errTail"
        exit 1
    }

    if (-not (Test-Path $OutFile) -or (Get-Item $OutFile).Length -eq 0) {
        # exit 0이어도 한도 메시지가 err에 있으면 quota로 분류 (일부 실패는 0으로 종료하고 출력만 비움)
        Write-Meta ($isQuota ? 'quota' : 'empty-output') $proc.ExitCode
        Write-Error "Codex가 응답 파일을 생성하지 않음$($isQuota ? ' (토큰/사용량 한도)' : '') — 검증 통과 아님"
        exit 1
    }

    Write-Meta 'success' $proc.ExitCode
    Write-Output "OK: $OutFile ($((Get-Item $OutFile).Length) bytes, $([math]::Round(((Get-Date)-$startedAt).TotalSeconds,1))s)"
    exit 0
}
catch {
    # 예외 경로에서도 한도 여부를 판별해 quota를 놓치지 않는다 (Start-Process/WaitForExit 예외 포함).
    Write-Meta ((Test-QuotaError) ? 'quota' : 'error') -1
    Write-Error "Codex 호출 예외: $($_.Exception.Message)"
    exit 1
}
