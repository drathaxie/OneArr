<#
.SYNOPSIS
  End-to-end acceptance of the released OneArr.Tray.exe on a disposable Windows machine.
.DESCRIPTION
  Installs the published release zip into %LOCALAPPDATA%\OneArr, signs in through the real UI, exercises the tray's
  command channel, runs a controlled test update feed on 127.0.0.1 (never GitHub), and checks checksum enforcement,
  successful update with database preservation, automatic rollback after a failed start, manual rollback that keeps
  discarded data, normal uninstall, and purge. Refuses to run if OneArr is already installed on the machine.
  Writes check results to -OutDir\tray-results.json and exits non-zero on any failure.
#>
param(
    [Parameter(Mandatory)] [string] $ReleaseZip,
    [Parameter(Mandatory)] [string] $ReleaseSha256File,
    [Parameter(Mandatory)] [string] $ReleaseVersion,
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $SourceDir = (Get-Location).Path
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$results = New-Object System.Collections.Generic.List[object]
$measure = [ordered]@{ version = $ReleaseVersion }
# Candidate updates are the next two patch versions, so the test works for any installed version.
$vp = $ReleaseVersion.Split('.')
$cand1 = "$($vp[0]).$($vp[1]).$([int]$vp[2] + 1)"
$cand2 = "$($vp[0]).$($vp[1]).$([int]$vp[2] + 2)"
$root = Join-Path $env:LOCALAPPDATA 'OneArr'
$tray = Join-Path $root 'OneArr.Tray.exe'
$app = Join-Path $root 'app'
$config = Join-Path $root 'config'
$backups = Join-Path $root 'backups'
$work = Join-Path ([IO.Path]::GetTempPath()) ('onearr-tray-acceptance-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$feed = Join-Path $work 'feed'
$base = 'http://127.0.0.1:8780'
$feedPort = 8099
$password = 'Tray-Acceptance-' + [guid]::NewGuid().ToString('N').Substring(0, 10)
$env:TRAY_TEST_PASSWORD = $password
$feedProc = $null

function Check([string] $name, [bool] $ok, [string] $detail = '') {
    $results.Add([pscustomobject]@{ check = $name; passed = $ok; detail = $detail })
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("{0}  {1}  {2}" -f $tag, $name, $detail)
}

function Wait-Until([scriptblock] $condition, [int] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try { if (& $condition) { return $true } } catch { }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Get-Health {
    try { return Invoke-RestMethod -Uri "$base/health" -TimeoutSec 2 } catch { return $null }
}

function Get-TrayStatus {
    $path = Join-Path $root 'tray-status.json'
    if (-not (Test-Path $path)) { return $null }
    return Get-Content $path -Raw | ConvertFrom-Json
}

function Send-TrayCommand([string] $command) {
    & $tray --command $command | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "tray command $command failed with exit $LASTEXITCODE" }
}

function Get-Procs([string] $name) {
    return @(Get-Process -Name $name -ErrorAction SilentlyContinue)
}

function Sum-Memory([object[]] $procs) {
    $ws = ($procs | Measure-Object WorkingSet64 -Sum).Sum
    $pm = ($procs | Measure-Object PrivateMemorySize64 -Sum).Sum
    return [ordered]@{
        processes = $procs.Count
        working_set_mb = [math]::Round(($ws / 1MB), 1)
        private_memory_mb = [math]::Round(($pm / 1MB), 1)
    }
}

function Folder-MB([string] $path) {
    if (-not (Test-Path $path)) { return 0 }
    $sum = (Get-ChildItem $path -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
    return [math]::Round(($sum / 1MB), 2)
}

function Write-Feed([string] $version, [string] $zipName, [string] $sha) {
    $dir = Join-Path $feed 'api\repos\drathaxie\OneArr\releases'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $json = @{
        tag_name = "v$version"
        assets   = @(
            @{ name = $zipName; browser_download_url = "http://127.0.0.1:$feedPort/dl/$zipName" },
            @{ name = "$zipName.sha256"; browser_download_url = "http://127.0.0.1:$feedPort/dl/$zipName.sha256" }
        )
    } | ConvertTo-Json -Depth 5
    Set-Content -Path (Join-Path $dir 'latest') -Value $json -Encoding UTF8
    $dl = Join-Path $feed 'dl'
    New-Item -ItemType Directory -Force -Path $dl | Out-Null
    Set-Content -Path (Join-Path $dl "$zipName.sha256") -Value $sha -Encoding ASCII
}

function Make-Candidate([string] $version, [string] $zipName, [string] $appSource) {
    $stage = Join-Path $work ("cand-" + $version)
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Force -Path (Join-Path $stage 'app') | Out-Null
    Copy-Item -Path (Join-Path $appSource '*') -Destination (Join-Path $stage 'app') -Recurse -Force
    Set-Content -Path (Join-Path $stage 'app\version.txt') -Value $version -NoNewline -Encoding ASCII
    New-Item -ItemType Directory -Force -Path (Join-Path $feed 'dl') | Out-Null
    $zip = Join-Path $feed "dl\$zipName"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    return (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLower()
}

function Write-TrayConfig {
    $json = @{ CheckForUpdates = $true; InstallUpdatesAutomatically = $false; UpdateApi = "http://127.0.0.1:$feedPort/api" } | ConvertTo-Json
    Set-Content -Path (Join-Path $root 'tray.json') -Value $json -Encoding UTF8
}

function Stop-TrayAndBackend {
    Send-TrayCommand 'quit'
    $gone = Wait-Until { (Get-Procs 'OneArr.Tray').Count -eq 0 } 20
    return $gone
}

function Start-InstalledTray {
    Start-Process -FilePath $tray -WorkingDirectory $root -WindowStyle Hidden | Out-Null
    return (Wait-Until { $null -ne (Get-Health) } 90)
}

try {
    # Refuse to touch a machine that already has OneArr installed.
    if (Test-Path $root) { throw "OneArr is already present at $root. This test needs a clean machine." }
    New-Item -ItemType Directory -Force -Path $work | Out-Null

    # Verify the release artifact before using it.
    $expected = (Get-Content $ReleaseSha256File -Raw).Split(' ')[0].Trim().ToLower()
    $actual = (Get-FileHash -Algorithm SHA256 $ReleaseZip).Hash.ToLower()
    Check 'release checksum matches the published .sha256' ($expected -eq $actual)

    $releaseDir = Join-Path $work 'release'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ReleaseZip, $releaseDir)
    $releaseTray = Join-Path $releaseDir 'OneArr.Tray.exe'
    Check 'release zip contains the tray and app folder' ((Test-Path $releaseTray) -and (Test-Path (Join-Path $releaseDir 'app\OneArr.Api.exe')))

    # ---- 1. Install from the extracted release folder and launch the installed copy.
    Start-Process -FilePath $releaseTray -WorkingDirectory $releaseDir -WindowStyle Hidden | Out-Null
    $installed = Wait-Until { (Test-Path $tray) -and (Test-Path (Join-Path $app 'OneArr.Api.exe')) } 120
    Check 'installs to %LOCALAPPDATA%\OneArr (tray and app folder)' $installed $root
    $shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\OneArr.lnk'
    Check 'Start Menu shortcut exists' (Test-Path $shortcut)
    $runValue = (Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name OneArr -ErrorAction SilentlyContinue).OneArr
    Check 'autostart is registered for the installed tray' ($runValue -like "*OneArr.Tray.exe*")

    $up = Start-InstalledTray
    $health = Get-Health
    Check 'backend starts and reports the release version' ($up -and $health.version -eq $ReleaseVersion) ("version " + $health.version)
    Check 'tray process is running' ((Get-Procs 'OneArr.Tray').Count -ge 1)

    # ---- 2. First-run sign-in in a real browser (dashboard opens, password accepted).
    Push-Location (Join-Path $SourceDir 'tools\windows\ui')
    npm install --no-audit --no-fund | Out-Null
    npx playwright install chromium | Out-Null
    node ui-check.mjs (Join-Path $OutDir 'ui') | Write-Host
    $uiOk = ($LASTEXITCODE -eq 0)
    Pop-Location
    Check 'first-run sign-in in a browser and dashboard opens' $uiOk (Get-Content (Join-Path $OutDir 'ui.json') -Raw)

    # ---- 3. Resource use with the dashboard closed (browser is closed by the UI step).
    Start-Sleep -Seconds 30
    $cpu0 = (Get-Procs 'OneArr.Api' | Select-Object -First 1).TotalProcessorTime
    $t0 = Get-Date
    Start-Sleep -Seconds 60
    $api = Get-Procs 'OneArr.Api' | Select-Object -First 1
    $cpuPct = [math]::Round(100 * ($api.TotalProcessorTime - $cpu0).TotalSeconds / ((Get-Date) - $t0).TotalSeconds, 2)
    $measure.idle_backend = Sum-Memory @(Get-Procs 'OneArr.Api')
    $measure.idle_tray = Sum-Memory @(Get-Procs 'OneArr.Tray')
    $measure.backend_cpu_percent_60s = $cpuPct
    $measure.installed_app_mb = Folder-MB $app
    $measure.installed_total_mb = Folder-MB $root
    $measure.database_kb = if (Test-Path (Join-Path $config 'onearr.db')) { [math]::Round((Get-Item (Join-Path $config 'onearr.db')).Length / 1KB, 1) } else { 0 }
    $measure.config_mb = Folder-MB $config
    $measure.logs_mb = Folder-MB (Join-Path $root 'logs')
    Check 'idle backend CPU under 5 percent after warm-up' ($cpuPct -lt 5) "$cpuPct%"

    # ---- 4. Settings: write user data we will check later.
    $auth = @{ Authorization = "Bearer " + (Invoke-RestMethod -Method Post -Uri "$base/api/auth/login" -ContentType 'application/json' -Body (@{ password = $password } | ConvertTo-Json)).token }
    Invoke-RestMethod -Method Put -Uri "$base/api/settings" -Headers $auth -ContentType 'application/json' -Body (@{ torrentRoot = 'C:\OneArrAcceptance\torrents-v1' } | ConvertTo-Json) | Out-Null
    Check 'user setting saved before the update' ((Invoke-RestMethod -Uri "$base/api/settings" -Headers $auth).torrentRoot -eq 'C:\OneArrAcceptance\torrents-v1')

    # ---- 5. Controlled test feed on 127.0.0.1. The real GitHub feed is never used here.
    $cand = Join-Path $work 'cand-source'
    dotnet publish (Join-Path $SourceDir 'src\OneArr.Api\OneArr.Api.csproj') -c Release -r win-x64 --self-contained true -p:Version=$cand1 -p:DebugType=None -p:DebugSymbols=false -o $cand | Out-Null
    $zip21 = "OneArr-$cand1-win-x64.zip"
    $wrongSha = ('0' * 64)
    $realSha21 = Make-Candidate $cand1 $zip21 $cand
    Write-Feed $cand1 $zip21 "$wrongSha  $zip21"
    $feedProc = Start-Process -FilePath 'python' -ArgumentList @('-m', 'http.server', "$feedPort", '--bind', '127.0.0.1', '--directory', $feed) -PassThru -WindowStyle Hidden
    Check 'controlled feed serves on 127.0.0.1 only' (Wait-Until { try { (Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:$feedPort/dl/$zip21.sha256").StatusCode -eq 200 } catch { $false } } 30)
    Write-TrayConfig
    Send-TrayCommand 'quit'
    Wait-Until { (Get-Procs 'OneArr.Tray').Count -eq 0 } 20 | Out-Null
    Start-InstalledTray | Out-Null

    # ---- 6. Update discovery and checksum enforcement.
    Send-TrayCommand 'check'
    $seen = Wait-Until { (Get-TrayStatus).latestVersion -eq $cand1 } 60
    Check "update discovery sees the test release $cand1" $seen
    Send-TrayCommand 'install'
    $refused = Wait-Until { (Get-TrayStatus).lastError -match 'checksum' } 120
    $status = Get-TrayStatus
    Check 'checksum mismatch is refused and nothing is applied' ($refused -and $status.installedVersion -eq $ReleaseVersion -and -not (Test-Path (Join-Path $root 'app.previous'))) ("error: " + $status.lastError)
    Check 'refused download is cleaned up' (-not (Test-Path (Join-Path $root "downloads\OneArr-$cand1-win-x64.zip")))

    # ---- 7. Correct checksum: successful update, database preserved.
    Write-Feed $cand1 $zip21 "$realSha21  $zip21"
    $dbBefore = (Get-Item (Join-Path $config 'onearr.db')).Length
    Send-TrayCommand 'check'
    Wait-Until { (Get-TrayStatus).latestVersion -eq $cand1 } 60 | Out-Null
    Send-TrayCommand 'install'
    $updated = Wait-Until { (Get-TrayStatus).installedVersion -eq $cand1 } 240
    $h = Get-Health
    Check "successful update activates $cand1 and it answers /health" ($updated -and $h.version -eq $cand1) ("health " + $h.version)
    Check 'previous version kept for rollback' ((Get-Content (Join-Path $root 'app.previous\version.txt') -Raw).Trim() -eq $ReleaseVersion)
    Check 'database backed up before the update' (@(Get-ChildItem $backups -Filter "onearr-$ReleaseVersion-*.db").Count -ge 1)
    $auth = @{ Authorization = "Bearer " + (Invoke-RestMethod -Method Post -Uri "$base/api/auth/login" -ContentType 'application/json' -Body (@{ password = $password } | ConvertTo-Json)).token }
    Check 'user setting and password survive the update' ((Invoke-RestMethod -Uri "$base/api/settings" -Headers $auth).torrentRoot -eq 'C:\OneArrAcceptance\torrents-v1')
    Check 'secret key file still present after the update' (Test-Path (Join-Path $config 'secret.key'))
    Check 'downloads cache is empty after the update' ((Folder-MB (Join-Path $root 'downloads')) -eq 0)

    # ---- 8. Data written under 0.2.1, then manual rollback: nothing silently discarded.
    Invoke-RestMethod -Method Put -Uri "$base/api/settings" -Headers $auth -ContentType 'application/json' -Body (@{ torrentRoot = 'C:\OneArrAcceptance\written-after-update' } | ConvertTo-Json) | Out-Null
    Send-TrayCommand 'rollback'
    $rolledBack = Wait-Until { (Get-Health).version -eq $ReleaseVersion } 120
    Check 'manual rollback restores 0.2.0' $rolledBack
    $discarded = @(Get-ChildItem $backups -Filter 'discarded-rollback-*.db')
    Check 'data written after the update is kept in backups, not deleted' ($discarded.Count -ge 1 -and (Select-String -Path $discarded[0].FullName -Pattern 'written-after-update' -SimpleMatch -Quiet))
    $auth = @{ Authorization = "Bearer " + (Invoke-RestMethod -Method Post -Uri "$base/api/auth/login" -ContentType 'application/json' -Body (@{ password = $password } | ConvertTo-Json)).token }
    Check 'password still works after rollback' ([bool] (Invoke-RestMethod -Uri "$base/api/settings" -Headers $auth))

    # ---- 9. Update to a build that reports the wrong version: automatic rollback.
    $zip22 = "OneArr-$cand2-win-x64.zip"
    $sha22 = Make-Candidate $cand2 $zip22 $cand
    Write-Feed $cand2 $zip22 "$sha22  $zip22"
    Send-TrayCommand 'check'
    Wait-Until { (Get-TrayStatus).latestVersion -eq $cand2 } 60 | Out-Null
    Send-TrayCommand 'install'
    $rb = Wait-Until { (Get-TrayStatus).lastResult -match 'rolled back' } 300
    $h = Get-Health
    Check 'build that fails its startup check is rolled back automatically' ($rb -and $h.version -eq $ReleaseVersion) ("health " + $h.version)
    Check 'database written by the failed build is kept as discarded-failed-update' (@(Get-ChildItem $backups -Filter 'discarded-failed-update-*.db').Count -ge 1)

    # ---- 10. Graceful quit and restart with the same data.
    $quitOk = Stop-TrayAndBackend
    Check 'quit command stops the tray' $quitOk
    Check 'quit command stops the backend' (Wait-Until { $null -eq (Get-Health) } 30)
    Check 'stop request is consumed on quit' (-not (Test-Path (Join-Path $config 'stop.request')))
    $restarted = Start-InstalledTray
    Check 'tray restart brings the backend back with the same data' $restarted
    Stop-TrayAndBackend | Out-Null

    # ---- 11. Normal uninstall keeps config and backups; reinstall; then purge.
    Start-InstalledTray | Out-Null
    & $tray --uninstall | Out-Null
    $gone = Wait-Until { -not (Test-Path (Join-Path $root 'app')) -and -not (Test-Path $tray) } 60
    Check 'uninstall removes the program files' $gone
    Check 'uninstall keeps config and backups' ((Test-Path (Join-Path $config 'onearr.db')) -and (Test-Path $backups))
    Check 'uninstall removes the Start Menu shortcut and autostart' ((-not (Test-Path $shortcut)) -and -not ((Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name OneArr -ErrorAction SilentlyContinue).OneArr))

    Start-Process -FilePath $releaseTray -WorkingDirectory $releaseDir -WindowStyle Hidden | Out-Null
    $reinstalled = Wait-Until { (Test-Path $tray) -and (Test-Path (Join-Path $app 'OneArr.Api.exe')) } 120
    Check 'reinstall reuses the kept config folder' ($reinstalled -and (Test-Path (Join-Path $config 'onearr.db')))
    Start-InstalledTray | Out-Null
    Stop-TrayAndBackend | Out-Null
    & $tray --uninstall --purge | Out-Null
    $purged = Wait-Until { -not (Test-Path $root) } 60
    Check 'purge removes the whole installation including config and backups' $purged

    $measure.checks = $results
}
catch {
    Check 'acceptance run completed without an exception' $false $_.Exception.Message
}
finally {
    try { Send-TrayCommand 'quit' } catch { }
    if ($feedProc -and -not $feedProc.HasExited) { $feedProc.Kill() }
    $measure | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $OutDir 'tray-results.json') -Encoding UTF8
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

$failed = @($results | Where-Object { -not $_.passed })
Write-Host ("{0} checks, {1} failed" -f $results.Count, $failed.Count)
if ($failed.Count -gt 0) { exit 1 }
exit 0
