#Requires -Version 7.0
<#
.SYNOPSIS
Copies OneArr's configuration (database with its WAL files, secret key, settings) into a new timestamped folder before an upgrade.

.DESCRIPTION
Read-only for the installation: it never deletes, moves or edits anything under the install root.
It refuses to run while OneArr is running, because a live SQLite database copied file by file can be inconsistent.
Every copy is verified by SHA-256 against its source. The result is a manifest.txt in the new folder.

.PARAMETER Destination
Folder that receives the new backup folder. Use a folder on another disk if you can.

.PARAMETER InstallRoot
Defaults to %LOCALAPPDATA%\OneArr, the location the tray installer uses.

.EXAMPLE
.\backup-onearr.ps1 -Destination D:\OneArr-Backups
#>
param(
    [Parameter(Mandatory)][string]$Destination,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'OneArr')
)

$ErrorActionPreference = 'Stop'
$config = Join-Path $InstallRoot 'config'
$database = Join-Path $config 'onearr.db'
$lock = Join-Path $config 'onearr.lock'

if (-not (Test-Path -LiteralPath $database)) { throw "No database found at $database. Check -InstallRoot." }

# OneArr holds onearr.lock open with FileShare.None while it runs. If we cannot open it exclusively, it is running.
if (Test-Path -LiteralPath $lock) {
    try {
        $probe = [System.IO.File]::Open($lock, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        $probe.Dispose()
    }
    catch {
        throw 'OneArr is running. Stop it from the tray (Stop OneArr) and run this script again. Nothing was copied.'
    }
}

$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$target = Join-Path $Destination "onearr-config-$stamp"
New-Item -ItemType Directory -Path $target -Force | Out-Null

# Transient files are not state: the lock and the stop request are recreated on every start.
$sources = Get-ChildItem -LiteralPath $config -File | Where-Object { $_.Name -notin @('onearr.lock', 'stop.request') }
if (-not $sources) { throw "Nothing to copy in $config." }

$manifest = [System.Collections.Generic.List[string]]::new()
$manifest.Add("OneArr configuration backup taken $stamp UTC")
$version = Join-Path $InstallRoot 'app\version.txt'
if (Test-Path -LiteralPath $version) { $manifest.Add("installed version: " + (Get-Content -LiteralPath $version -Raw).Trim()) }
$manifest.Add('')

foreach ($file in $sources) {
    $copy = Join-Path $target $file.Name
    Copy-Item -LiteralPath $file.FullName -Destination $copy
    $sourceHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $copyHash = (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash
    if ($sourceHash -ne $copyHash) { throw "Verification failed for $($file.Name). The backup is incomplete; do not upgrade." }
    $manifest.Add("$copyHash  $($file.Length)  $($file.Name)")
}

$manifest | Set-Content -LiteralPath (Join-Path $target 'manifest.txt') -Encoding utf8
Write-Host "Backup complete and verified: $target"
Write-Host ($sources | ForEach-Object { '  {0,-18} {1,12:N0} bytes' -f $_.Name, $_.Length } | Out-String).TrimEnd()
Write-Host 'Original media files were not read or changed. Keep this folder until the new version has been checked.'
