param(
    [string]$Library,
    [switch]$AllUsers
    )

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') {
    throw 'Run this installer on Windows.'
    }

if (-not $Library) {
    $Library = Join-Path $PSScriptRoot 'MNXFile.Native.dll'
    if (-not (Test-Path -LiteralPath $Library -PathType Leaf)) {
        $mnxParent = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
        $Library = Join-Path $mnxParent 'MNX_Builds\mnxfile\win-x64\MNXFile.Native.dll'
        }
    }

if (-not (Test-Path -LiteralPath $Library -PathType Leaf)) {
    throw ('Native engine not found: ' + $Library)
    }

$mnxSource = (Resolve-Path -LiteralPath $Library).Path
if ($AllUsers) {
    $mnxDestination = Join-Path ([Environment]::GetFolderPath('CommonProgramFiles')) 'MNXFile'
    }
else {
    $mnxDestination = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\MNXFile'
    }

New-Item -ItemType Directory -Force -Path $mnxDestination | Out-Null
$mnxInstalledLibrary = Join-Path $mnxDestination 'MNXFile.Native.dll'
Copy-Item -LiteralPath $mnxSource -Destination $mnxInstalledLibrary -Force
$mnxSourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $mnxSource).Hash
$mnxInstalledHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $mnxInstalledLibrary).Hash
if ($mnxSourceHash -ne $mnxInstalledHash) {
    throw 'The installed engine checksum does not match its source.'
    }

Write-Host ('Installed MNXFile engine: ' + $mnxInstalledLibrary)
Write-Host 'MNX clients discover this installation automatically. No PATH or environment changes are needed.'
