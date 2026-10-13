<#
  TiaApiServer 빌드 (버전별).

  왜 exe 가 2개인가
  ─────────────────
  V19 와 V20 은 어셈블리 이름이 `Siemens.Engineering` 으로 같다. 그래서 V20 으로
  한 번 컴파일해 두면 런타임에 AssemblyResolve 가 V19 경로로 갈아끼울 수 있다
  (`--tia=19`). **V20 빌드 하나가 V19 와 V20 을 모두 커버한다.**

  V19 레퍼런스로 따로 컴파일하지는 않는다 — Program.cs 가 V20 에서 추가된 API를
  쓰기 때문이다(MrpInstancesOwner · AttributeAccessOptions · DriveObjectNumber ·
  LibraryTypeVersion.ExportAsDocuments · LibraryTypeComposition.CreateFromDocuments
  등). V19 어셈블리로 컴파일하면 그 호출들이 전부 깨진다. 즉 V19 지원은 예나
  지금이나 "V20 으로 빌드해서 V19 에 붙이는" 런타임 지원이다.

  V21 은 단일 어셈블리가 Base/Step7/WinCC/WinCCUnified/Startdrive 로 쪼개지고
  경로도 PublicAPI\V21\net48 로 깊어진다. 어셈블리 '이름' 이 달라서 같은 수를
  쓸 수 없으므로 V21 만 따로 빌드한다.

  사용:
    .\build.ps1                 설치된 것 전부 (V20, V21)
    .\build.ps1 -Versions 21    V21 만
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

# 버전별로 반드시 있어야 하는 Openness 어셈블리
function Get-RequiredAssemblies([int] $major) {
  $base = "C:\Program Files\Siemens\Automation\Portal V$major\PublicAPI\V$major"
  if ($major -ge 21) {
    $net = Join-Path $base 'net48'
    return @(
      (Join-Path $net 'Siemens.Engineering.Base.dll'),
      (Join-Path $net 'Siemens.Engineering.Step7.dll'),
      (Join-Path $net 'Siemens.Engineering.WinCC.dll'),
      (Join-Path $net 'Siemens.Engineering.WinCCUnified.dll'),
      (Join-Path $net 'Siemens.Engineering.Startdrive.dll')
    )
  }
  return @(
    (Join-Path $base 'Siemens.Engineering.dll'),
    (Join-Path $base 'Siemens.Engineering.Hmi.dll')
  )
}

function Test-Openness([int] $major, [ref] $missing) {
  $gone = @()
  foreach ($f in (Get-RequiredAssemblies $major)) {
    if (-not (Test-Path $f)) { $gone += (Split-Path $f -Leaf) }
  }
  $contract = "C:\Program Files\Siemens\Automation\Portal V$major\Bin\PublicAPI\Siemens.Engineering.Contract.dll"
  if (-not (Test-Path $contract)) { $gone += 'Siemens.Engineering.Contract.dll' }
  $missing.Value = $gone
  return $gone.Count -eq 0
}

$msbuild = Find-MSBuild
Write-Host "MSBuild: $msbuild" -ForegroundColor DarkGray
Write-Host ''

# V19 는 빌드 대상이 아니다 (위 주석 참고). V20 빌드가 V19 를 커버한다.
if (-not $Versions) { $Versions = 20, 21 }

$built = @()
$skipped = @()

foreach ($v in $Versions) {
  if ($v -eq 19) {
    Write-Host 'V19 는 따로 빌드하지 않습니다.' -ForegroundColor Yellow
    Write-Host '  Program.cs 가 V20 에서 추가된 API 를 써서 V19 레퍼런스로는 컴파일되지 않습니다.' -ForegroundColor Yellow
    Write-Host '  V20 빌드가 V19 를 커버합니다:  TiaApiServer.V20.exe 8099 --tia=19' -ForegroundColor Yellow
    Write-Host ''
    $skipped += 'V19 (V20 빌드가 커버)'
    continue
  }

  $proj = Join-Path $root "TiaApiServer.V$v.csproj"
  if (-not (Test-Path $proj)) { $skipped += "V${v} (csproj 없음)"; continue }

  $missing = @()
  if (-not (Test-Openness $v ([ref] $missing))) {
    $skipped += "V${v} (없는 어셈블리: $($missing -join ', '))"
    continue
  }

  Write-Host "==> TiaApiServer.V$v" -ForegroundColor Cyan
  & $msbuild $proj /p:Configuration=$Configuration /v:minimal /nologo
  if ($LASTEXITCODE -ne 0) { throw "V$v 빌드 실패" }
  $built += "V$v"
}

Write-Host ''
if ($built)   { Write-Host "빌드 완료: $($built -join ', ')" -ForegroundColor Green }
if ($skipped) { Write-Host "건너뜀: $($skipped -join ', ')" -ForegroundColor Yellow }
Write-Host "출력: $(Join-Path $root "bin\$Configuration")" -ForegroundColor DarkGray
Write-Host ''
Write-Host '실행 예:' -ForegroundColor DarkGray
Write-Host '  .\bin\Release\TiaApiServer.V21.exe 8099            # V21' -ForegroundColor DarkGray
Write-Host '  .\bin\Release\TiaApiServer.V20.exe 8099            # V20' -ForegroundColor DarkGray
Write-Host '  .\bin\Release\TiaApiServer.V20.exe 8099 --tia=19   # V19' -ForegroundColor DarkGray
