<#
.SYNOPSIS
  Proves OneArr's complete manual download workflow against a release-shaped build, using only
  isolated temp folders, generated fixtures, and stub upstream servers (qBittorrent + TMDb).
  No real media, no real torrents, no real indexers, no secrets.
.DESCRIPTION
  Stages: setup -> settings -> profile -> stub client + connection test -> add series (stub
  TMDb) -> wanted shows both episodes -> manual grab -> stub client reports downloading ->
  queue sync -> restart -> stub reports completed -> queue sync -> import preview (plans only,
  nothing written) -> explicit confirmed apply -> file association -> library available ->
  wanted cleared -> restart -> everything still consistent. Also asserts automatic grabbing
  stays off and dry-run stays the default throughout.
  Writes check results to -OutFile as JSON and exits non-zero if any check fails.
  Requires only Windows PowerShell 5.1 or PowerShell 7.
#>
param(
    [Parameter(Mandatory)] [string] $ReleaseDir,
    [string] $OutFile = "lifecycle.json",
    [int] $Port = 18781,
    [int] $StubPort = 18080
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$apiExe = Join-Path $ReleaseDir 'app\OneArr.Api.exe'
if (-not (Test-Path $apiExe)) { throw "Missing $apiExe" }
$base = "http://127.0.0.1:$Port"
$stubBase = "http://127.0.0.1:$StubPort"
$work = Join-Path ([IO.Path]::GetTempPath()) ("onearr-lifecycle-" + [guid]::NewGuid().ToString('N'))
$config = Join-Path $work 'config'
$torrents = Join-Path $work 'torrents'
$media = Join-Path $work 'media'
$stateFile = Join-Path $work 'qb-state.json'
$callsFile = Join-Path $work 'qb-calls.txt'
$checks = New-Object System.Collections.Generic.List[object]

function Check([string] $name, [bool] $ok, [string] $detail = '') {
    $checks.Add([pscustomobject]@{ check = $name; passed = $ok; detail = $detail })
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("{0}  {1} {2}" -f $tag, $name, $detail)
}

function Wait-Health([string] $url, [int] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try { Invoke-RestMethod -Uri $url -TimeoutSec 2 | Out-Null; return $true } catch { Start-Sleep -Milliseconds 250 }
    }
    return $false
}

function Api([string] $method, [string] $path, $body, [string] $token) {
    $headers = @{ Accept = 'application/json' }
    if ($token) { $headers['Authorization'] = "Bearer $token" }
    $params = @{ Method = $method; Uri = "$base$path"; Headers = $headers; TimeoutSec = 30 }
    if ($null -ne $body) { $params['ContentType'] = 'application/json'; $params['Body'] = ($body | ConvertTo-Json -Depth 6) }
    try { return Invoke-RestMethod @params }
    catch {
        $detail = $_.ErrorDetails.Message
        if (-not $detail) { $detail = $_.Exception.Message }
        throw "API $method $path failed: $detail"
    }
}

function Start-Backend {
    $env:ONEARR_CONFIG_DIR = $config
    $env:ONEARR_PORT = "$Port"
    $env:ONEARR_LISTEN_ADDRESS = '127.0.0.1'
    $env:ONEARR_TMDB_BASE_URL = "$stubBase/"
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    $p = Start-Process -FilePath $apiExe -WorkingDirectory (Split-Path $apiExe) -PassThru -WindowStyle Hidden
    if (-not (Wait-Health "$base/health" 60)) { throw "Backend did not become healthy" }
    return $p
}

function Stop-Backend($proc) {
    if ($null -eq $proc -or $proc.HasExited) { return }
    New-Item -ItemType File -Path (Join-Path $config 'stop.request') -Force | Out-Null
    if (-not $proc.WaitForExit(20000)) { $proc.Kill() }
}

function Write-QbState($torrentsList) {
    # Write-then-rename so the stub never reads a torn state file mid-write.
    $tmp = "$stateFile.tmp"
    @{ torrents = $torrentsList } | ConvertTo-Json -Depth 5 | Set-Content -Encoding ascii $tmp
    Move-Item -Force $tmp $stateFile
}

$stubJob = $null
$proc = $null
try {
    New-Item -ItemType Directory -Path $config, $torrents, (Join-Path $media 'tv') -Force | Out-Null
    # Fixture episode file, aged so the 30 s quiet period does not defer its import.
    $fixtureDir = Join-Path $torrents 'Show'
    New-Item -ItemType Directory -Path $fixtureDir -Force | Out-Null
    $fixture = Join-Path $fixtureDir 'Show - S01E01.mkv'
    'fixture episode bytes' | Set-Content -NoNewline -Encoding ascii $fixture
    (Get-Item $fixture).LastWriteTime = (Get-Date).AddMinutes(-5)
    Write-QbState @()
    '' | Set-Content -Encoding ascii $callsFile

    $stubScript = {
        param($StubPort, $StateFile, $CallsFile)
        $listener = New-Object System.Net.HttpListener
        $listener.Prefixes.Add("http://127.0.0.1:$StubPort/")
        $listener.Start()
        while ($listener.IsListening) {
            try { $ctx = $listener.GetContext() } catch { break }
            $req = $ctx.Request
            $res = $ctx.Response
            $path = $req.Url.AbsolutePath
            $body = ''
            $status = 200
            $contentType = 'application/json'
            try {
                if ($path -eq '/health') { $body = '{"ok":true}'; $contentType = 'application/json' }
                elseif ($path -eq '/api/v2/auth/login') {
                    $body = 'Ok.'; $contentType = 'text/plain'
                    $res.Cookies.Add((New-Object System.Net.Cookie('SID', 'fakesid', '/', '127.0.0.1')))
                }
                elseif ($path -eq '/api/v2/torrents/add') {
                    Add-Content -Encoding ascii -Path $CallsFile -Value 'add'
                    $body = 'Ok.'; $contentType = 'text/plain'
                }
                elseif ($path -eq '/api/v2/torrents/info') {
                    $cookie = $req.Headers['Cookie']
                    if (-not $cookie -or $cookie -notmatch 'SID=fakesid') { $status = 403; $body = 'Forbidden'; $contentType = 'text/plain' }
                    else {
                        $category = ''
                        $query = $req.Url.Query
                        if ($query -match 'category=([^&]+)') { $category = [Uri]::UnescapeDataString($matches[1]) }
                        $state = Get-Content -Raw -Encoding ascii -Path $StateFile | ConvertFrom-Json
                        $rows = @($state.torrents | Where-Object { $_.category -eq $category })
                        if ($rows.Count -eq 0) { $body = '[]' } else { $body = ConvertTo-Json -Depth 5 $rows }
                    }
                }
                elseif ($path -eq '/tv/4242') {
                    $body = '{"id":4242,"name":"Show","first_air_date":"2010-01-01","overview":"Fixture show","poster_path":null,"number_of_seasons":1,"seasons":[{"season_number":1}]}'
                }
                elseif ($path -eq '/tv/4242/external_ids') { $body = '{"imdb_id":null,"tvdb_id":null}' }
                elseif ($path -eq '/tv/4242/season/1') {
                    $body = '{"episodes":[{"episode_number":1,"id":101,"name":"Pilot","air_date":"2010-01-01"},{"episode_number":2,"id":102,"name":"Second","air_date":"2010-01-08"}]}'
                }
                else { $status = 404; $body = '{"error":"not found"}' }
            } catch {
                # A bad stub request must never kill the stub: answer 500 and keep serving.
                $status = 500; $body = '{"error":"stub failure"}'; $contentType = 'application/json'
            }
            $bytes = [Text.Encoding]::UTF8.GetBytes($body)
            $res.StatusCode = $status
            $res.ContentType = $contentType
            $res.ContentLength64 = $bytes.Length
            $res.OutputStream.Write($bytes, 0, $bytes.Length)
            $res.OutputStream.Close()
        }
    }
    $stubJob = Start-Job -ScriptBlock $stubScript -ArgumentList $StubPort, $stateFile, $callsFile
    if (-not (Wait-Health "$stubBase/health" 30)) { throw "Stub upstream servers did not start" }
    Check 'stub upstream servers (qBittorrent + TMDb fixtures)' $true

    $proc = Start-Backend
    Check 'backend starts against throwaway config' ($null -ne $proc -and -not $proc.HasExited)

    $setup = Api 'POST' '/api/auth/setup' @{ password = 'correct horse battery' } ''
    $token = $setup.token
    Check 'first-run setup issues a token' ($null -ne $token -and $token.Length -gt 0)

    # PUT /api/settings returns 204 on success; verify through a GET instead of the empty body.
    Api 'PUT' '/api/settings' @{ tmdbApiKey = 'fixture-key'; torrentRoot = $torrents; mediaRoot = $media } $token | Out-Null
    $settings = Api 'GET' '/api/settings' $null $token
    Check 'settings accept stub roots and fixture key' ($settings.torrentRoot -eq $torrents -and $settings.mediaRoot -eq $media -and $settings.tmdbConfigured -eq $true)

    $profile = Api 'POST' '/api/quality-profiles' @{ name = 'HD-1080p'; minResolution = 720; cutoffResolution = 1080; allowWebSources = $true } $token
    Check 'quality profile created' ($profile.id -gt 0) ("id=$($profile.id)")

    Api 'POST' '/api/download-clients' @{ name = 'stub'; baseUrl = $stubBase; username = 'u'; password = 'p'; movieCategory = 'onearr-movies'; tvCategory = 'onearr-tv'; enabled = $true } $token | Out-Null
    $clients = Api 'GET' '/api/download-clients' $null $token
    $client = @($clients | Where-Object { $_.name -eq 'stub' })[0]
    Check 'stub download client configured' ($null -ne $client -and $client.id -gt 0)
    $connTest = Api 'POST' '/api/connections/test' @{ service = 'qbittorrent'; clientId = $client.id } $token
    Check 'stub qBittorrent connection test passes' ($connTest.ok -eq $true) ($connTest.detail)

    $added = Api 'POST' '/api/media' @{ type = 'series'; providerId = '4242'; qualityProfileId = $profile.id; rootFolder = (Join-Path $media 'tv'); monitored = $true; allSeasons = $true } $token
    $seriesId = $added.item.id
    Check 'series added from stub TMDb' ($null -ne $seriesId -and $added.item.title -eq 'Show') ("title=$($added.item.title)")
    $detail = Api 'GET' "/api/media/$seriesId/detail" $null $token
    $ep1 = @($detail.episodes | Where-Object { $_.episodeNumber -eq 1 })[0]
    $ep2 = @($detail.episodes | Where-Object { $_.episodeNumber -eq 2 })[0]
    Check 'stub series carries two episodes' ($null -ne $ep1 -and $null -ne $ep2)

    $wanted = Api 'GET' '/api/wanted' $null $token
    $wantedEp1 = @($wanted.episodes | Where-Object { $_.episodeId -eq $ep1.id }).Count -gt 0
    $wantedEp2 = @($wanted.episodes | Where-Object { $_.episodeId -eq $ep2.id }).Count -gt 0
    Check 'wanted lists both episodes before any grab' ($wantedEp1 -and $wantedEp2)

    $hash = 'a' * 40
    $grab = Api 'POST' '/api/grab' @{ mediaItemId = $seriesId; episodeId = $ep1.id; title = 'Show S01E01 1080p WEB-DL'; link = "magnet:?xt=urn:btih:$hash"; infoHash = $hash } $token
    $jobId = $grab.id
    Check 'manual grab creates a job' ($null -ne $jobId) ("job=$jobId")
    $calls = Get-Content -Encoding ascii -Path $callsFile
    Check 'grab reached the stub download client' ($calls -contains 'add')

    Write-QbState @(@{ hash = $hash; name = 'Show S01E01 1080p WEB-DL'; category = 'onearr-tv'; state = 'downloading'; progress = 0.5; content_path = $null; size = 100; dlspeed = 2500000; eta = 600 })
    Api 'POST' '/api/queue/sync' $null $token | Out-Null
    $queue = Api 'GET' '/api/queue' $null $token
    $job = @($queue.jobs | Where-Object { $_.id -eq $jobId })[0]
    Check 'queue sync reports downloading with real progress' ($job.status -eq 'Downloading' -and $job.progress -eq 0.5) ("status=$($job.status) progress=$($job.progress)")
    Check 'queue reports client speed and ETA from the stub' ($job.downloadSpeedBps -eq 2500000 -and $job.etaSeconds -eq 600)

    Stop-Backend $proc
    $proc = Start-Backend
    $queue = Api 'GET' '/api/queue' $null $token
    $job = @($queue.jobs | Where-Object { $_.id -eq $jobId })[0]
    Check 'download survives a backend restart' ($job.status -eq 'Downloading')

    Write-QbState @(@{ hash = $hash; name = 'Show S01E01 1080p WEB-DL'; category = 'onearr-tv'; state = 'uploading'; progress = 1; content_path = (Join-Path $torrents 'Show'); size = 100; dlspeed = 0; eta = -1 })
    Api 'POST' '/api/queue/sync' $null $token | Out-Null
    $queue = Api 'GET' '/api/queue' $null $token
    $job = @($queue.jobs | Where-Object { $_.id -eq $jobId })[0]
    Check 'queue sync reports completed' ($job.status -eq 'Completed') ("status=$($job.status)")

    $preview = Api 'POST' "/api/queue/$jobId/import-preview" $null $token
    $dest = Join-Path $media 'tv\Show\Season 01\Show - S01E01.mkv'
    $plan = $preview.plans[0]
    Check 'import preview plans one hardlink for S01E01' ($preview.plans.Count -eq 1 -and $plan.plannedAction -eq 'hardlink' -and ($plan.matchedEpisodeLabels -contains 'S01E01')) ("action=$($plan.plannedAction)")
    Check 'preview writes nothing' (-not (Test-Path $dest))

    $applied = Api 'POST' "/api/queue/$jobId/import-apply" @{ previewId = $preview.previewId; confirmed = $true } $token
    Check 'confirmed apply succeeds' ($null -ne $applied)
    $queue = Api 'GET' '/api/queue' $null $token
    $job = @($queue.jobs | Where-Object { $_.id -eq $jobId })[0]
    Check 'job becomes Imported after apply' ($job.status -eq 'Imported') ("status=$($job.status)")
    Check 'source fixture survives the import' (Test-Path $fixture)
    Check 'destination file exists after apply' (Test-Path $dest)

    $files = Api 'GET' "/api/media/$seriesId/files" $null $token
    $filesJson = $files | ConvertTo-Json -Depth 5
    Check 'library files list the imported file' ($filesJson -match 'Show - S01E01.mkv')
    $detail = Api 'GET' "/api/media/$seriesId/detail" $null $token
    $ep1After = @($detail.episodes | Where-Object { $_.episodeNumber -eq 1 })[0]
    $ep2After = @($detail.episodes | Where-Object { $_.episodeNumber -eq 2 })[0]
    Check 'episode 1 becomes available, episode 2 stays missing' ($ep1After.hasFile -eq $true -and $ep2After.hasFile -eq $false)
    $wanted = Api 'GET' '/api/wanted' $null $token
    $wantedEp1After = @($wanted.episodes | Where-Object { $_.episodeId -eq $ep1.id }).Count -gt 0
    $wantedEp2After = @($wanted.episodes | Where-Object { $_.episodeId -eq $ep2.id }).Count -gt 0
    Check 'wanted clears episode 1 and keeps episode 2' ((-not $wantedEp1After) -and $wantedEp2After)

    $settingsAfter = Api 'GET' '/api/settings' $null $token
    Check 'automatic grabbing stays off' ($settingsAfter.automaticGrabbing -eq $false)
    Check 'dry-run import stays the default' ($settingsAfter.dryRunImport -eq $true)

    Stop-Backend $proc
    $proc = Start-Backend
    $queue = Api 'GET' '/api/queue' $null $token
    $job = @($queue.jobs | Where-Object { $_.id -eq $jobId })[0]
    $wanted = Api 'GET' '/api/wanted' $null $token
    $wantedEp1Final = @($wanted.episodes | Where-Object { $_.episodeId -eq $ep1.id }).Count -gt 0
    Check 'imported state survives a second restart' ($job.status -eq 'Imported' -and -not $wantedEp1Final)
}
finally {
    if ($null -ne $proc) { Stop-Backend $proc }
    if ($null -ne $stubJob) { Stop-Job $stubJob -ErrorAction SilentlyContinue; Remove-Job $stubJob -Force -ErrorAction SilentlyContinue }
    $failed = @($checks | Where-Object { -not $_.passed })
    @{ checks = $checks; failed = $failed.Count } | ConvertTo-Json -Depth 5 | Set-Content -Encoding ascii $OutFile
    if (Test-Path $work) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}

if (@($checks | Where-Object { -not $_.passed }).Count -gt 0) { throw "Lifecycle acceptance failed" }
Write-Host "Lifecycle acceptance passed"
