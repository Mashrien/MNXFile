# Locate Visual Studio's x64 C++ compiler for NativeAOT and C ABI smoke tests.
$ErrorActionPreference = 'Stop'
if (Get-Command cl.exe -ErrorAction SilentlyContinue) {
    return
    }

$mnxVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $mnxVswhere -PathType Leaf)) {
    throw 'Install Visual Studio C++ build tools and a Windows SDK before running a native build.'
    }

$mnxCompilerSetup = @(& $mnxVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'VC\Auxiliary\Build\vcvars64.bat')
if ($mnxCompilerSetup.Count -eq 0) {
    throw 'Visual Studio is installed, but its x64 C++ tools are missing.'
    }

$mnxEnvironmentFile = Join-Path ([IO.Path]::GetTempPath()) ('mnx-toolchain-' + [Guid]::NewGuid().ToString('N') + '.cmd')
try {
    @('@echo off', ('call "' + $mnxCompilerSetup[0] + '" >nul'), 'if errorlevel 1 exit /b 1', 'set') | Set-Content -Encoding ASCII -LiteralPath $mnxEnvironmentFile
    $mnxEnvironmentLines = & cmd.exe /d /c $mnxEnvironmentFile
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to initialize the Visual Studio x64 build environment.'
        }

    foreach ($mnxEnvironmentLine in $mnxEnvironmentLines) {
        if ($mnxEnvironmentLine -match '^([^=]+)=(.*)$') {
            [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process')
            }
        }
    }
finally {
    Remove-Item -LiteralPath $mnxEnvironmentFile -ErrorAction SilentlyContinue
    }
