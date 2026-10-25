<#
.SYNOPSIS
  Runs a release folder's backend headless and checks what a Windows user relies on: startup, health, sign-in,
  dashboard, settings persistence across restart, the single-instance lock, graceful shutdown, and resource use.
.DESCRIPTION
  Uses a throwaway config folder and a non-default port. Never touches a real installation. Writes measurements to
  -OutFile as JSON and exits non-zero if any check fails. Requires only Windows PowerShell 5.1 or PowerShell 7.
#>
param(
    [Parameter(Mandatory)] [string] $ReleaseDir,
    [Parameter(Mandatory)] [string] $ExpectedVersion,
    [string] $OutFile = "measurements.json",
    [int] $Port = 18780
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$api = Join-Path $ReleaseDir 'app\OneArr.Api.exe'
if (-not (Test-Path $api)) { throw "Missing $api" }
$base = "http://127.0.0.1:$Port"
$config = Join-Path ([IO.Path]::GetTempPath()) ("onearr-accept-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $config | Out-Null
$checks = New-Object System.Collections.Generic.List[object]
$m = [ordered]@{ version = $ExpectedVersion; os = [Environment]::OSVersion.VersionString; machine = $env:COMPUTERNAME }

function Check([string] $name, [bool] $ok, [string] $detail = '') {
    $checks.Add([pscustomobject]@{ check = $name; passed = $ok; detail = $detail })
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("{0}  {1} {2}" -f $tag, $name, $detail)
}

function Set-OneArrEnv {
    $env:ONEARR_CONFIG_DIR = $config
    $env:ONEARR_PORT = "$Port"
    $env:ONEARR_LISTEN_ADDRESS = '127.0.0.1'
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
}

function Wait-Health([int] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try { return Invoke-RestMethod -Uri "$base/health" -TimeoutSec 2 } catch { Start-Sleep -Milliseconds 250 }
    }
    return $null
}

function Start-Backend {
    Set-OneArrEnv
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $api -WorkingDirectory (Split-Path $api) -PassThru -WindowStyle Hidden
    $health = Wait-Health 60
    $sw.Stop()
    return [pscustomobject]@{ Process = $p; Health = $health; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 2) }
}

function Stop-Gracefully($proc) {
    New-Item -ItemType File -Path (Join-Path $config 'stop.request') -Force | Out-Null
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $exited = $proc.WaitForExit(20000)
    $sw.Stop()
    return [pscustomobject]@{ Exited = $exited; ExitCode = if ($exited) { $proc.ExitCode } else { $null }; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 2) }
}

$backend = $null
try {
    # 1. Startup and health
    $backend = Start-Backend
    Check 'backend starts and answers /health' ($null -ne $backend.Health) "startup $($backend.Seconds)s"
    $m.startup_seconds = $backend.Seconds
    Check 'health reports the release version' ($backend.Health.version -eq $ExpectedVersion) "reported $($backend.Health.version)"
    Check 'health reports database ok' ($backend.Health.database -eq 'ok')

    # 2. Web UI is served, API requires sign-in
    $html = Invoke-WebRequest -Uri "$base/" -UseBasicParsing -TimeoutSec 5
    Check 'web UI is served at /' ($html.Content -match '<div id="root">')
    $unauth = try { (Invoke-WebRequest -Uri "$base/api/dashboard" -UseBasicParsing -TimeoutSec 5).StatusCode } catch { $_.Exception.Response.StatusCode.value__ }
    Check 'dashboard requires sign-in' ($unauth -eq 401) "status $unauth"

    # 3. First-run setup, dashboard, settings write
    $password = 'acceptance-test-password-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $setup = Invoke-RestMethod -Method Post -Uri "$base/api/auth/setup" -ContentType 'application/json' -Body (@{ password = $password } | ConvertTo-Json)
    $token = $setup.token
    $auth = @{ Authorization = "Bearer $token" }
    Check 'first-run setup issues a token' ([bool] $token)
    $dash = Invoke-RestMethod -Uri "$base/api/dashboard" -Headers $auth -TimeoutSec 5
    Check 'dashboard loads with the token' ($null -ne $dash.movies)
    Invoke-RestMethod -Method Put -Uri "$base/api/settings" -Headers $auth -ContentType 'application/json' -Body (@{ dryRunImport = $false; mediaRoot = 'C:\OneArrAcceptance\media' } | ConvertTo-Json) | Out-Null
    $settings = Invoke-RestMethod -Uri "$base/api/settings" -Headers $auth
    Check 'settings save and read back' ($settings.dryRunImport -eq $false)

    # 4. Resource use while idle (no UI open, no work queued)
    $cpuBefore = $backend.Process.TotalProcessorTime
    $wallBefore = Get-Date
    Start-Sleep -Seconds 20
    $backend.Process.Refresh()
    $cpuSeconds = ($backend.Process.TotalProcessorTime - $cpuBefore).TotalSeconds
    $wall = ((Get-Date) - $wallBefore).TotalSeconds
    $m.idle = [ordered]@{
        working_set_mb = [math]::Round($backend.Process.WorkingSet64 / 1MB, 1)
        private_memory_mb = [math]::Round($backend.Process.PrivateMemorySize64 / 1MB, 1)
        cpu_percent_over_20s = [math]::Round(100 * $cpuSeconds / $wall, 2)
        threads = $backend.Process.Threads.Count
        onearr_processes = @(Get-Process -Name 'OneArr.Api' -ErrorAction SilentlyContinue).Count
    }
    Check 'idle CPU under 5 percent' ($m.idle.cpu_percent_over_20s -lt 5) "$($m.idle.cpu_percent_over_20s)%"
    Check 'idle private memory under 250 MB' ($m.idle.private_memory_mb -lt 250) "$($m.idle.private_memory_mb) MB"

    # 5. Second backend on the same configuration must refuse to start
    Set-OneArrEnv
    $second = Start-Process -FilePath $api -WorkingDirectory (Split-Path $api) -PassThru -WindowStyle Hidden
    $refused = $second.WaitForExit(15000)
    Check 'second instance on the same configuration refuses to start' ($refused -and $second.ExitCode -eq 3) "exit $($second.ExitCode)"
    if (-not $refused) { $second.Kill() }

    # 6. Graceful shutdown, then restart with the same data
    $stop = Stop-Gracefully $backend.Process
    $m.shutdown = [ordered]@{ seconds = $stop.Seconds; exit_code = $stop.ExitCode }
    Check 'graceful stop exits with code 0' ($stop.Exited -and $stop.ExitCode -eq 0) "exit $($stop.ExitCode) after $($stop.Seconds)s"
    Check 'stop request file was consumed' (-not (Test-Path (Join-Path $config 'stop.request')))

    $backend = Start-Backend
    Check 'backend restarts after a clean stop' ($null -ne $backend.Health)
    $login = Invoke-RestMethod -Method Post -Uri "$base/api/auth/login" -ContentType 'application/json' -Body (@{ password = $password } | ConvertTo-Json)
    Check 'password still works after restart' ([bool] $login.token)
    $settings2 = Invoke-RestMethod -Uri "$base/api/settings" -Headers @{ Authorization = "Bearer $($login.token)" }
    Check 'settings persisted across restart' ($settings2.dryRunImport -eq $false)

    # 7. Footprint
    $db = Join-Path $config 'onearr.db'
    $m.footprint = [ordered]@{
        release_folder_mb = [math]::Round(((Get-ChildItem $ReleaseDir -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
        release_file_count = @(Get-ChildItem $ReleaseDir -Recurse -File).Count
        database_kb = if (Test-Path $db) { [math]::Round((Get-Item $db).Length / 1KB, 1) } else { 0 }
        config_folder_kb = [math]::Round(((Get-ChildItem $config -Recurse -File | Measure-Object Length -Sum).Sum) / 1KB, 1)
    }
}
catch {
    Check 'acceptance run completed without an exception' $false $_.Exception.Message
}
finally {
    if ($backend -and -not $backend.Process.HasExited) {
        $null = Stop-Gracefully $backend.Process
        if (-not $backend.Process.HasExited) { $backend.Process.Kill() }
    }
    $m.checks = $checks
    $m | ConvertTo-Json -Depth 6 | Set-Content -Path $OutFile -Encoding UTF8
    Remove-Item -Recurse -Force $config -ErrorAction SilentlyContinue
}

$failed = @($checks | Where-Object { -not $_.passed })
Write-Host ("{0} checks, {1} failed" -f $checks.Count, $failed.Count)
if ($failed.Count -gt 0) { exit 1 }
exit 0
