<#
  TiaInspector 빌드.

  설치돼 있는 TIA 버전만 골라 빌드한다 — V21 이 없는 머신에서 V21 을 빌드하려
  들면 HintPath 가 깨져 실패하기 때문이다. 런처는 항상 빌드한다(Openness 를
  참조하지 않으므로 TIA 설치와 무관).

  사용:
    .\build.ps1                 설치된 버전 전부 + 런처
    .\build.ps1 -Versions 20    V20 만 + 런처
    .\build.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
  [int[]] $Versions,
  [ValidateSet('Release', 'Debug')]
  [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Find-MSBuild {
  $vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
  if (Test-Path $vswhere) {
    $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
      -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if ($found) { return $found }
  }
  $fallback = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'
  if (Test-Path $fallback) { return $fallback }
  throw 'MSBuild 를 찾지 못했습니다.'
}

function Test-OpennessInstalled([int] $major) {
  $base = "C:\Program Files\Siemens\Automation\Portal V$major\PublicAPI\V$major"
  if ($major -ge 21) {
    return Test-Path (Join-Path $base 'net48\Siemens.Engineering.Base.dll')
  }
  return Test-Path (Join-Path $base 'Siemens.Engineering.dll')
}

$msbuild = Find-MSBuild
Write-Host "MSBuild: $msbuild" -ForegroundColor DarkGray
Write-Host ''

if (-not $Versions) { $Versions = 19, 20, 21 }

$built = @()
$skipped = @()

foreach ($v in $Versions) {
  $proj = Join-Path $root "TiaInspector.V$v.csproj"
  if (-not (Test-Path $proj)) { $skipped += "V${v} (csproj 없음)"; continue }
  if (-not (Test-OpennessInstalled $v)) { $skipped += "V${v} (Openness 미설치)"; continue }

  Write-Host "==> TiaInspector.V$v" -ForegroundColor Cyan
  & $msbuild $proj /p:Configuration=$Configuration /v:minimal /nologo
  if ($LASTEXITCODE -ne 0) { throw "V$v 빌드 실패" }
  $built += "V$v"
}

Write-Host '==> TiaInspector.Launcher' -ForegroundColor Cyan
& $msbuild (Join-Path $root 'TiaInspector.Launcher\TiaInspector.Launcher.csproj') `
  /p:Configuration=$Configuration /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw '런처 빌드 실패' }

Write-Host ''
Write-Host "빌드 완료: $($built -join ', ') + 런처" -ForegroundColor Green
if ($skipped) { Write-Host "건너뜀: $($skipped -join ', ')" -ForegroundColor Yellow }
Write-Host "출력: $(Join-Path $root "bin\$Configuration")" -ForegroundColor DarkGray
