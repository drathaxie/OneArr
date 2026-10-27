<#
.SYNOPSIS
  Staging test against explicitly configured real services: TMDb, a Torznab indexer and qBittorrent WebUI.
  Run it on the Windows machine that can reach your qBittorrent. It never runs in CI (secrets stay on your machine).
.DESCRIPTION
  Uses a disposable config folder and disposable torrent and media folders under a staging root you choose.
  Automatic grabbing stays off. A grab happens only when STAGING_TEST_MAGNET is set to authorized test content you
  chose. Every credential is read from an environment variable and is never written to the results file.
  Required environment variables:
    STAGING_ROOT            disposable folder for config, torrents and media (created if missing)
    STAGING_TMDB_KEY        TMDb API key or v4 read token
    STAGING_TMDB_QUERY      a title to search, for example "The Matrix"
    STAGING_INDEXER_URL     Torznab endpoint, for example https://indexer.example/api/...
    STAGING_INDEXER_KEY     indexer API key
    STAGING_QBIT_URL        qBittorrent WebUI base URL, for example http://192.168.1.10:8080
    STAGING_QBIT_USER       qBittorrent username
    STAGING_QBIT_PASS       qBittorrent password
  Optional:
    STAGING_TEST_MAGNET     magnet link for AUTHORIZED test content only (enables the grab and import steps)
    STAGING_MEDIA_TITLE     title of the test item to add (defaults to STAGING_TMDB_QUERY)
    STAGING_ALLOW_REAL_IMPORT=1  allow a real import into the staging media folder (dry run otherwise)
#>
param(
    [string] $OneArrDir = (Join-Path $PSScriptRoot '..\..\build\stage\app'),
    [string] $OutFile = 'staging-results.json',
    [int] $Port = 18781
)

$ErrorActionPreference = 'Stop'
$results = New-Object System.Collections.Generic.List[object]
function Check([string] $name, [bool] $ok, [string] $detail = '') {
    $results.Add([pscustomobject]@{ check = $name; passed = $ok; detail = $detail })
    Write-Host ("{0}  {1}  {2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $detail)
}
function Need([string] $name) {
    $v = [Environment]::GetEnvironmentVariable($name)
    if ([string]::IsNullOrWhiteSpace($v)) { throw "Missing required environment variable $name" }
    return $v
}

# ---- Preconditions (fail before anything is started).
$stagingRoot = Need 'STAGING_ROOT'
$tmdbKey = Need 'STAGING_TMDB_KEY'
$tmdbQuery = Need 'STAGING_TMDB_QUERY'
$indexerUrl = Need 'STAGING_INDEXER_URL'
$indexerKey = Need 'STAGING_INDEXER_KEY'
$qbitUrl = Need 'STAGING_QBIT_URL'
$qbitUser = Need 'STAGING_QBIT_USER'
$qbitPass = Need 'STAGING_QBIT_PASS'
$testMagnet = [Environment]::GetEnvironmentVariable('STAGING_TEST_MAGNET')
$mediaTitle = [Environment]::GetEnvironmentVariable('STAGING_MEDIA_TITLE'); if (-not $mediaTitle) { $mediaTitle = $tmdbQuery }
$allowReal = [Environment]::GetEnvironmentVariable('STAGING_ALLOW_REAL_IMPORT') -eq '1'

$exe = Join-Path $OneArrDir 'OneArr.Api.exe'
if (-not (Test-Path $exe)) { throw "OneArr.Api.exe not found at $exe (point -OneArrDir at a release app folder)" }
$stagingRoot = [IO.Path]::GetFullPath($stagingRoot)
if ($stagingRoot -match '^[A-Za-z]:\\?$' -or $stagingRoot.Length -lt 12) { throw "STAGING_ROOT '$stagingRoot' is too broad. Use a dedicated folder." }
$config = Join-Path $stagingRoot 'config'
$torrents = Join-Path $stagingRoot 'torrents'
$media = Join-Path $stagingRoot 'media'
New-Item -ItemType Directory -Force -Path $config, $torrents, (Join-Path $media 'movies') | Out-Null
$base = "http://127.0.0.1:$Port"
$password = 'staging-' + [guid]::NewGuid().ToString('N').Substring(0, 12)

# Credentials are sent only to OneArr over loopback, and never echoed.
$env:ONEARR_CONFIG_DIR = $config
$env:ONEARR_PORT = "$Port"
$env:ONEARR_LISTEN_ADDRESS = '127.0.0.1'
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$proc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru -WindowStyle Hidden
try {
    $ready = $false
    for ($i = 0; $i -lt 120 -and -not $ready; $i++) {
        try { Invoke-RestMethod -Uri "$base/health" -TimeoutSec 2 | Out-Null; $ready = $true } catch { Start-Sleep -Milliseconds 500 }
    }
    Check 'backend starts with a disposable configuration' $ready
    if (-not $ready) { throw 'backend did not start' }

    $token = (Invoke-RestMethod -Method Post -Uri "$base/api/auth/setup" -ContentType 'application/json' -Body (@{ password = $password } | ConvertTo-Json)).token
    $auth = @{ Authorization = "Bearer $token" }
    $json = 'application/json'

    Invoke-RestMethod -Method Put -Uri "$base/api/settings" -Headers $auth -ContentType $json -Body (@{
        tmdbApiKey = $tmdbKey; torrentRoot = $torrents; mediaRoot = $media; dryRunImport = $true; automaticGrabbing = $false
    } | ConvertTo-Json) | Out-Null
    $s = Invoke-RestMethod -Uri "$base/api/settings" -Headers $auth
    Check 'automatic grabbing is OFF for the whole staging run' ($s.automaticGrabbing -eq $false)
    Check 'TMDb key is stored and never echoed back' ($s.tmdbConfigured -eq $true -and -not ($s | ConvertTo-Json -Depth 4).Contains($tmdbKey))

    # 1. TMDb: authenticated metadata search.
    try {
        $hits = Invoke-RestMethod -Uri "$base/api/metadata/search?type=movie&q=$([uri]::EscapeDataString($tmdbQuery))" -Headers $auth
        Check 'TMDb search authenticates and returns results' (@($hits).Count -gt 0) "$(@($hits).Count) results"
    } catch { Check 'TMDb search authenticates and returns results' $false ($_.ErrorDetails.Message) }

    # 2. Add the test item (movie), so the release search has a real target.
    $hit = @($hits) | Where-Object { $_.title -eq $mediaTitle } | Select-Object -First 1
    if (-not $hit) { $hit = @($hits) | Select-Object -First 1 }
    $add = Invoke-RestMethod -Method Post -Uri "$base/api/media" -Headers $auth -ContentType $json -Body (@{
        type = 'movie'; providerId = $hit.providerId; qualityProfileId = 1; rootFolder = (Join-Path $media 'movies'); monitored = $true
    } | ConvertTo-Json)
    Check 'test item added with a stable TMDb identity' ([bool] $add.item.id) "id $($add.item.id)"
    $mid = $add.item.id

    # 3. Indexer: save it (key stored server-side), then run a manual release search.
    Invoke-RestMethod -Method Post -Uri "$base/api/indexers" -Headers $auth -ContentType $json -Body (@{
        name = 'staging-indexer'; baseUrl = $indexerUrl; apiKey = $indexerKey; enabled = $true
    } | ConvertTo-Json) | Out-Null
    $search = Invoke-RestMethod -Method Post -Uri "$base/api/search" -Headers $auth -ContentType $json -Body (@{ mediaItemId = $mid } | ConvertTo-Json)
    $errs = @($search.indexerErrors)
    Check 'indexer answers the Torznab search without error' ($errs.Count -eq 0) ($(if ($errs.Count) { 'indexer error: ' + $errs[0].message } else { "$(@($search.results).Count) releases" }))
    $accepted = @($search.results | Where-Object { $_.accepted }).Count
    $rejected = @($search.results | Where-Object { -not $_.accepted })
    Check 'release parsing and decisions produced (accepted and rejected with reasons)' ((@($search.results).Count -gt 0) -and (@($rejected | Where-Object { $_.reasons.Count -eq 0 }).Count -eq 0)) "accepted $accepted, rejected $($rejected.Count)"

    # 4. qBittorrent: save the client, then prove login and queue read work.
    Invoke-RestMethod -Method Post -Uri "$base/api/download-clients" -Headers $auth -ContentType $json -Body (@{
        name = 'staging-qbit'; baseUrl = $qbitUrl; username = $qbitUser; password = $qbitPass
        movieCategory = 'onearr-movies'; tvCategory = 'onearr-tv'; enabled = $true
    } | ConvertTo-Json) | Out-Null
    try {
        Invoke-RestMethod -Method Post -Uri "$base/api/queue/sync" -Headers $auth | Out-Null
        Check 'qBittorrent login and queue synchronization work' $true
    } catch {
        Check 'qBittorrent login and queue synchronization work' $false ($_.ErrorDetails.Message)
    }

    # 5. Grab and import, only for authorized test content the operator supplied.
    if ($testMagnet) {
        $best = @($search.results | Where-Object { $_.accepted }) | Select-Object -First 1
        if (-not $best) {
            Check 'an accepted release exists for the grab step' $false 'no accepted release; grab skipped'
        } else {
            $job = Invoke-RestMethod -Method Post -Uri "$base/api/grab" -Headers $auth -ContentType $json -Body (@{
                mediaItemId = $mid; title = $best.title; link = $testMagnet; infoHash = $best.infoHash
            } | ConvertTo-Json)
            Check 'manual grab submitted to qBittorrent' ([bool] $job.id) "job $($job.id)"
            $done = $false
            for ($i = 0; $i -lt 180 -and -not $done; $i++) {
                Start-Sleep -Seconds 10
                Invoke-RestMethod -Method Post -Uri "$base/api/queue/sync" -Headers $auth | Out-Null
                $q = @(Invoke-RestMethod -Uri "$base/api/queue" -Headers $auth | Where-Object { $_.id -eq $job.id })
                $done = $q.Count -gt 0 -and ($q[0].status -eq 'Completed' -or $q[0].status -eq 'Imported')
            }
            Check 'queue reports the test download as completed (30 min limit)' $done
            $dry = Invoke-RestMethod -Method Post -Uri "$base/api/import?dryRun=true" -Headers $auth
            Check 'dry-run import plans the file without writing' ($dry.dryRun -eq $true) $dry.message
            if ($done -and $allowReal) {
                $preview = Invoke-RestMethod -Method Post -Uri "$base/api/queue/$($job.id)/import-preview" -Headers $auth
                $bulkBody = @{ confirmed = $true; approvals = @(@{ jobId = $job.id; previewId = $preview.previewId }) } | ConvertTo-Json
                Invoke-RestMethod -Method Post -Uri "$base/api/import?dryRun=false" -Headers $auth -ContentType $json -Body $bulkBody | Out-Null
                $imported = @(Get-ChildItem (Join-Path $media 'movies') -Recurse -File).Count
                Check 'real import wrote the file into the staging media folder' ($imported -ge 1) "$imported file(s)"
            }
        }
    } else {
        Check 'grab and import steps were not run (STAGING_TEST_MAGNET not set)' $true 'skipped by design'
    }

    # 6. Confirm the run still did not auto-grab anything.
    $final = Invoke-RestMethod -Uri "$base/api/settings" -Headers $auth
    Check 'automatic grabbing still OFF at the end' ($final.automaticGrabbing -eq $false)
}
catch {
    Check 'staging run completed without an exception' $false $_.Exception.Message
}
finally {
    if (-not $proc.HasExited) {
        New-Item -ItemType File -Path (Join-Path $config 'stop.request') -Force | Out-Null
        if (-not $proc.WaitForExit(20000)) { $proc.Kill() }
    }
    $results | ConvertTo-Json -Depth 4 | Set-Content -Path $OutFile -Encoding UTF8
}
$failed = @($results | Where-Object { -not $_.passed })
Write-Host ("{0} checks, {1} failed. Results: {2}" -f $results.Count, $failed.Count, $OutFile)
if ($failed.Count -gt 0) { exit 1 }
exit 0
