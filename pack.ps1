# ServerLib NuGet 패키지 생성 스크립트
# 사용: pwsh ./pack.ps1   (선택: -Version 1.0.1)
# 산출물: nupkgs/ServerLib.<Version>.nupkg  (DLL + XML 문서 주석 동봉)
param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$proj = Join-Path $PSScriptRoot "ServerLib/ServerLib.csproj"
$out  = Join-Path $PSScriptRoot "nupkgs"

$args = @($proj, "-c", "Release", "-o", $out)
if ($Version -ne "") { $args += "-p:Version=$Version" }

dotnet pack @args
Write-Host "패키지 생성 완료 → $out" -ForegroundColor Green
