param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [switch]$Clean,
    [switch]$Test,
    [switch]$PackagesOnly
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Build Windows Native AOT on Windows with the .NET 10 SDK and Visual Studio C++ build tools.' }
. (Join-Path $PSScriptRoot 'scripts/initialize-windows-build.ps1')
$mnxRoot = $PSScriptRoot
$mnxParent = Split-Path -Parent $mnxRoot
$mnxComponent = 'mnxfile'
$mnxPrimary = 'MNXFile.csproj'
if ($PackagesOnly -and $mnxComponent -ne 'mnxfile') { throw '-PackagesOnly applies to MNXFile only.' }
$mnxSdk = & dotnet --version
if ($LASTEXITCODE -ne 0 -or [int]($mnxSdk.Split('.')[0]) -lt 10) { throw '.NET SDK 10 or newer is required.' }
if (-not $env:MNXBuildRoot) { $env:MNXBuildRoot = Join-Path $mnxParent "mnx_clutter/build/$mnxComponent" }
if (-not $env:MNXReleaseRoot) { $env:MNXReleaseRoot = Join-Path $mnxParent "MNX_Builds/$mnxComponent" }
New-Item -ItemType Directory -Force -Path $env:MNXBuildRoot, $env:MNXReleaseRoot | Out-Null
$mnxLock = [IO.File]::Open((Join-Path $env:MNXBuildRoot 'release.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
$mnxOriginalBuildNumber = $env:MNXBuildNumber
try {
    $env:MSBUILDDISABLENODEREUSE = '1'
    if (-not $env:MNXBuildNumber) {
        $mnxReserved = & dotnet msbuild (Join-Path $mnxRoot $mnxPrimary) -nologo -verbosity:quiet -target:ReserveMNXBuildNumber -getProperty:MNXBuildNumber
        if ($LASTEXITCODE -ne 0) { throw 'Build-number reservation failed.' }
        $env:MNXBuildNumber = ($mnxReserved -join "`n").Trim()
    }
    if ($env:MNXBuildNumber -notmatch '^\d+$' -or [int]$env:MNXBuildNumber -lt 1 -or [int]$env:MNXBuildNumber -gt 65534) { throw 'MNXBuildNumber must be 1..65534.' }
    $mnxOutput = Join-Path $env:MNXReleaseRoot $Runtime
    New-Item -ItemType Directory -Force -Path $mnxOutput | Out-Null
    $mnxFeed = $env:MNXPackageFeed
    if (-not $mnxFeed) { $mnxFeed = Join-Path $mnxParent 'MNX_Builds/mnxfile/packages' }
    $mnxFeed = [Security.SecurityElement]::Escape($mnxFeed)
    $mnxConfig = Join-Path $env:MNXBuildRoot 'nuget.config'
    $mnxSources = '<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />'
    if ($mnxComponent -ne 'mnxfile') { $mnxSources = '<add key="MNX client feed" value="' + $mnxFeed + '" />' + $mnxSources }
    [IO.File]::WriteAllText($mnxConfig, '<configuration><packageSources><clear />' + $mnxSources + '</packageSources></configuration>')
    $mnxOptions = @('-c', 'Release', '-p:UseSharedCompilation=false', "-p:RestoreConfigFile=$mnxConfig")
    function Invoke-MNXDotnet {
        & dotnet @args
        if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
    }
    Write-Host "Building $mnxComponent release $env:MNXBuildNumber for $Runtime"
    if ($Clean) {
        # Remove both RID-specific publish intermediates and RID-independent package builds.
        foreach ($mnxGeneratedFolder in @('bin', 'obj')) {
            $mnxGeneratedPath = Join-Path $env:MNXBuildRoot $mnxGeneratedFolder
            if (Test-Path -LiteralPath $mnxGeneratedPath) {
                Remove-Item -LiteralPath $mnxGeneratedPath -Recurse -Force
                }
            }
        foreach ($mnxCleanProject in @('MNXFile.csproj', 'client/MNXFile.Client.csproj', 'native/MNXFile.Native.csproj')) {
            Invoke-MNXDotnet restore (Join-Path $mnxRoot $mnxCleanProject) -r $Runtime "-p:RestoreConfigFile=$mnxConfig"
            Invoke-MNXDotnet clean (Join-Path $mnxRoot $mnxCleanProject) -c Release -r $Runtime -nologo
        }
    }

    New-Item -ItemType Directory -Force -Path (Join-Path $env:MNXReleaseRoot 'packages') | Out-Null
    foreach ($mnxProject in @('MNXFile.csproj', 'client/MNXFile.Client.csproj')) {
        Invoke-MNXDotnet pack (Join-Path $mnxRoot $mnxProject) @mnxOptions -o (Join-Path $env:MNXReleaseRoot 'packages')
    }
    if (-not $PackagesOnly) {
        Invoke-MNXDotnet publish (Join-Path $mnxRoot 'native/MNXFile.Native.csproj') @mnxOptions -r $Runtime -o $mnxOutput
        Copy-Item (Join-Path $mnxRoot 'native/include/mnxfile.h') $mnxOutput
        Copy-Item (Join-Path $mnxRoot 'scripts/install-runtime.ps1') $mnxOutput
    }
    if ($Test) {
        Invoke-MNXDotnet run --project (Join-Path $mnxRoot 'tests/MNXFile.Smoke/MNXFile.Smoke.csproj') @mnxOptions
        if (-not $PackagesOnly) {
            $mnxPreviousLibrary = $env:MNXFILE_NATIVE_LIBRARY
            try {
                $env:MNXFILE_NATIVE_LIBRARY = Join-Path $mnxOutput 'MNXFile.Native.dll'
                Invoke-MNXDotnet run --project (Join-Path $mnxRoot 'tests/MNXFile.Client.Smoke/MNXFile.Client.Smoke.csproj') @mnxOptions
            } finally { $env:MNXFILE_NATIVE_LIBRARY = $mnxPreviousLibrary }
            $mnxTestOutput = Join-Path $env:MNXBuildRoot "tests/$Runtime"
            New-Item -ItemType Directory -Force -Path $mnxTestOutput | Out-Null
            $mnxSmoke = Join-Path $mnxTestOutput 'native-smoke.exe'
            & cl /nologo /std:c11 /W4 /WX "/I$(Join-Path $mnxRoot 'native/include')" `
                (Join-Path $mnxRoot 'tests/native/smoke.c') "/Fo$(Join-Path $mnxTestOutput 'native-smoke.obj')" "/Fe$mnxSmoke" `
                /link (Join-Path $mnxOutput 'MNXFile.Native.lib')
            if ($LASTEXITCODE -ne 0) { throw 'Native C smoke compilation failed.' }
            $mnxTestWork = Join-Path ([IO.Path]::GetTempPath()) ('mnx-native-' + [Guid]::NewGuid().ToString('N'))
            $mnxPreviousPath = $env:PATH
            New-Item -ItemType Directory -Path $mnxTestWork | Out-Null
            try {
                $env:PATH = $mnxOutput + [IO.Path]::PathSeparator + $env:PATH
                & $mnxSmoke $mnxTestWork
                if ($LASTEXITCODE -ne 0) { throw 'Native C smoke failed.' }
            } finally {
                $env:PATH = $mnxPreviousPath
                Remove-Item -Recurse -Force $mnxTestWork
            }
        }
    }

    $mnxSymbols = Join-Path $env:MNXBuildRoot "symbols/$Runtime"
    New-Item -ItemType Directory -Force -Path $mnxSymbols | Out-Null
    Get-ChildItem -LiteralPath $mnxOutput -File | Where-Object { $_.Extension -in '.pdb', '.dbg' } | Move-Item -Destination $mnxSymbols -Force
    Write-Host "Release output: $env:MNXReleaseRoot"
} finally {
    $env:MNXBuildNumber = $mnxOriginalBuildNumber
    $mnxLock.Dispose()
}
