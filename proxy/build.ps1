<#
.SYNOPSIS
    Builds the version.dll proxy and its test harness.

.DESCRIPTION
    Locates a Visual Studio installation with the x64 C++ tools, then builds:

      bin\x64\version.dll    the proxy itself
      bin\x64\proxytest.exe  compares the proxy's answers with the system version.dll

    The proxy has to match the architecture of the target application, which is x64 for
    the current Fiddler Everywhere builds.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Debug
#>

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('x64')]
    [string]$Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path $root "bin\$Architecture"

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe was not found, so the Visual Studio tools cannot be located."
}

$vsPath = & $vswhere -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath

if (-not $vsPath) {
    throw "No Visual Studio installation with the x64 C++ tools was found."
}

$vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) {
    throw "vcvars64.bat was not found under $vsPath."
}

Write-Host "Visual Studio: $vsPath"

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

$commonFlags = '/nologo /W4 /EHsc /std:c++17 /DUNICODE /D_UNICODE'
$releaseFlags = if ($Configuration -eq 'Release') { '/O2 /MD' } else { '/Od /Zi /MDd /RTC1' }

$buildDll = @"
call "$vcvars" >nul && cl $commonFlags $releaseFlags /LD /Fo"$outputDir\\" src\version_proxy.cpp /link /DEF:src\version_proxy.def /OUT:"$outputDir\version.dll" /IMPLIB:"$outputDir\version.lib"
"@

$buildTest = @"
call "$vcvars" >nul && cl $commonFlags $releaseFlags /Fo"$outputDir\\" test\proxytest.cpp /link /OUT:"$outputDir\proxytest.exe"
"@

Push-Location $root
try {
    Write-Host "Building version.dll..."
    cmd /c $buildDll
    if ($LASTEXITCODE -ne 0) {
        throw "The proxy build failed with exit code $LASTEXITCODE."
    }

    Write-Host "Building proxytest.exe..."
    cmd /c $buildTest
    if ($LASTEXITCODE -ne 0) {
        throw "The test build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "Built:"
Get-ChildItem $outputDir -File | ForEach-Object { "  {0,-16} {1,10} bytes" -f $_.Name, $_.Length }
