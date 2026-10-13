<#
  TiaInspector 실행 파일을 TIA Openness AllowList 에 등록한다.
  **관리자 권한으로 실행해야 한다** (HKLM 쓰기).

  왜 필요한가
  -----------
  이미 떠 있는 TIA Portal 에 attach 할 때, AllowList 에 없는 애플리케이션은
  세션은 맺어지지만 `TiaPortal.Projects` 가 빈 컬렉션으로 돌아온다 —
  예외도 없고 대화상자도 없어서 "CPU 0대" 로만 보인다.
  (실측: 같은 코드로 직접 띄운 헤드리스 포탈에서는 Projects 가 정상.)

  키 구조 (실측 — 기존 등록 항목에서 역산)
  -----------------------------------------------------
  HKLM\SOFTWARE\Siemens\Automation\Openness\AllowList\<파일명>\Entry
      Path         REG_SZ  실행 파일 전체 경로
      DateModified REG_SZ  yyyy/MM/dd HH:mm:ss.fff
      FileHash     REG_SZ  base64( SHA-256(파일내용) )

  주의: 해시가 파일 내용에 묶여 있으므로 **다시 빌드할 때마다 이 스크립트를
  다시 실행**해야 한다.

  사용:
    .\register-openness.ps1              등록
    .\register-openness.ps1 -Remove      해제
    .\register-openness.ps1 -List        현재 등록 목록만 보기
#>
[CmdletBinding()]
param(
  [switch] $Remove,
  [switch] $List,
  [string] $BinDir
)

$ErrorActionPreference = 'Stop'
$AllowListKey = 'HKLM:\SOFTWARE\Siemens\Automation\Openness\AllowList'

function Test-Admin {
  $id = [Security.Principal.WindowsIdentity]::GetCurrent()
  (New-Object Security.Principal.WindowsPrincipal $id).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Show-AllowList {
  Write-Host '현재 AllowList:' -ForegroundColor Cyan
  if (-not (Test-Path $AllowListKey)) { Write-Host '  (키 없음)'; return }
  $any = $false
  foreach ($k in Get-ChildItem $AllowListKey -ErrorAction SilentlyContinue) {
    $any = $true
    $entry = Join-Path $k.PSPath 'Entry'
    if (Test-Path $entry) {
      $p = Get-ItemProperty $entry
      Write-Host "  $($k.PSChildName)"
      Write-Host "      $($p.Path)" -ForegroundColor DarkGray
    }
    else { Write-Host "  $($k.PSChildName)  (Entry 없음)" }
  }
  if (-not $any) { Write-Host '  (비어 있음)' }
}

if ($List) { Show-AllowList; return }

if (-not (Test-Admin)) {
  Write-Host '관리자 권한이 필요합니다. 관리자 PowerShell 에서 다시 실행하세요:' -ForegroundColor Red
  Write-Host "  powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -ForegroundColor Yellow
  exit 1
}

if (-not $BinDir) { $BinDir = Join-Path $PSScriptRoot 'bin\Release' }
$exes = Get-ChildItem (Join-Path $BinDir 'TiaInspector*.exe') -ErrorAction SilentlyContinue
if (-not $exes) { throw "실행 파일을 찾지 못했습니다: $BinDir  (먼저 .\build.ps1 을 실행하세요)" }

foreach ($exe in $exes) {
  $keyPath = Join-Path $AllowListKey $exe.Name

  if ($Remove) {
    if (Test-Path $keyPath) {
      Remove-Item $keyPath -Recurse -Force
      Write-Host "해제: $($exe.Name)" -ForegroundColor Yellow
    }
    continue
  }

  $sha = [System.Security.Cryptography.SHA256]::Create()
  $hash = [Convert]::ToBase64String($sha.ComputeHash([System.IO.File]::ReadAllBytes($exe.FullName)))
  $stamp = $exe.LastWriteTime.ToString('yyyy/MM/dd HH:mm:ss.fff')

  $entryPath = Join-Path $keyPath 'Entry'
  if (-not (Test-Path $entryPath)) { New-Item -Path $entryPath -Force | Out-Null }
  New-ItemProperty -Path $entryPath -Name 'Path'         -Value $exe.FullName -PropertyType String -Force | Out-Null
  New-ItemProperty -Path $entryPath -Name 'DateModified' -Value $stamp        -PropertyType String -Force | Out-Null
  New-ItemProperty -Path $entryPath -Name 'FileHash'     -Value $hash         -PropertyType String -Force | Out-Null

  Write-Host "등록: $($exe.Name)" -ForegroundColor Green
  Write-Host "      $($exe.FullName)" -ForegroundColor DarkGray
  Write-Host "      SHA-256 $hash" -ForegroundColor DarkGray
}

Write-Host ''
Show-AllowList
if (-not $Remove) {
  Write-Host ''
  Write-Host '등록을 반영하려면 TIA Portal 을 다시 시작해야 할 수 있습니다.' -ForegroundColor Cyan
  Write-Host '다시 빌드하면 해시가 바뀌므로 이 스크립트를 다시 실행하세요.' -ForegroundColor Cyan
}
