#Requires -Version 7.0
<#
.SYNOPSIS
Preview, and optionally apply, the library reconciliation of a running OneArr against real local media folders.
Automatic grabbing is never enabled or started by this script.

.DESCRIPTION
Steps: check settings, optionally add path mappings and reassign root folders, run a read-only preview, write a report,
then apply only after you type APPLY (and ACKNOWLEDGE if there are unmatched files). Before and after snapshots are saved
so you can see which items changed. Original media files are never written to.

.PARAMETER Mapping
Path mappings written as 'movie|<Sonarr/Radarr path>|<path on this PC>' or 'series|...'. Separate several with ';'.
Existing mappings are kept; a mapping with the same type and source replaces the old target.

.PARAMETER RootFrom
Move catalog entries whose RootFolder is exactly this value...
.PARAMETER RootTo
...to this value. Requires -Kind. Changes OneArr's record only.

.PARAMETER Kind
movie or series. Used with -RootFrom and -RootTo.

.PARAMETER BaseUrl
Where OneArr listens. Defaults to the installed backend.

.PARAMETER OutDir
Folder for reports and snapshots. Defaults to %LOCALAPPDATA%\OneArr\validation.
#>
param(
    [string[]]$Mapping = @(),
    [string]$RootFrom,
    [string]$RootTo,
    [ValidateSet('movie', 'series')][string]$Kind,
    [string]$BaseUrl = 'http://127.0.0.1:8780',
    [string]$OutDir = (Join-Path $env:LOCALAPPDATA 'OneArr\validation')
)

$ErrorActionPreference = 'Stop'
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$run = Join-Path $OutDir $stamp
New-Item -ItemType Directory -Path $run -Force | Out-Null
$report = [System.Collections.Generic.List[string]]::new()
function Say([string]$line) { Write-Host $line; $report.Add($line) }

function Read-Answer([string]$Prompt) {
    # Read-Host ignores redirected input and returns an empty string, which is never consent. Read the console line instead.
    if ([Console]::IsInputRedirected) {
        Write-Host "${Prompt}: " -NoNewline
        $line = [Console]::In.ReadLine()
        Write-Host $line
        return [string]$line
    }
    return Read-Host $Prompt
}
function Read-Secret([string]$Prompt) {
    if ([Console]::IsInputRedirected) { return [string][Console]::In.ReadLine() }
    $secure = Read-Host $Prompt -AsSecureString
    return ConvertFrom-SecureString -SecureString $secure -AsPlainText
}
function Invoke-OneArr {
    param([string]$Method, [string]$Path, $Body)
    $args = @{ Method = $Method; Uri = "$BaseUrl$Path"; Headers = @{ Authorization = "Bearer $script:Token" }; ErrorAction = 'Stop' }
    if ($null -ne $Body) { $args.Body = ($Body | ConvertTo-Json -Depth 8 -AsArray:$false); $args.ContentType = 'application/json' }
    try {
        $result = Invoke-RestMethod @args
        # A JSON array comes back as one object; return it whole so callers can loop over it.
        if ($result -is [System.Array]) { return , $result }
        return $result
    }
    catch {
        $detail = $_.ErrorDetails.Message
        throw "OneArr returned an error for $Method ${Path}: $detail"
    }
}

# 1. Connection and sign-in. The password is read here and kept in memory only.
try { $health = Invoke-RestMethod -Uri "$BaseUrl/health" -ErrorAction Stop } catch { throw "OneArr is not answering at $BaseUrl. Start it from the tray first." }
$password = Read-Secret 'OneArr admin password'
$login = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/auth/login" -ContentType 'application/json' -Body (@{ password = $password } | ConvertTo-Json)
$script:Token = $login.token
$password = $null
Say "OneArr version $($health.version) at $BaseUrl"

# 2. Automatic grabbing must be off before anything else. This script does not change it.
$settings = Invoke-OneArr -Method Get -Path '/api/settings'
if ($settings.automaticGrabbing) { throw 'Automatic grabbing is ON. Turn it off in Settings and run this again. Nothing was changed.' }
Say 'Automatic grabbing: off (checked, not changed)'

# 3. Path mappings and root reassignment. Both change OneArr's records only.
if ($Mapping.Count -gt 0) {
    $current = @(Invoke-OneArr -Method Get -Path '/api/library/status').mappings
    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($m in $current) { $rows.Add(@{ type = $m.mediaType.ToLowerInvariant(); sourcePrefix = $m.sourcePrefix; targetPrefix = $m.targetPrefix }) }
    # Entries may be separated by ';' so the list also works from the command line under pwsh -File.
    foreach ($spec in @($Mapping | ForEach-Object { $_.Split(';') } | Where-Object { $_.Trim() })) {
        $parts = $spec.Trim().Split('|')
        if ($parts.Count -ne 3) { throw "Mapping '$spec' must look like movie|/data/media/movies|D:\Media\Movies" }
        $kept = @($rows | Where-Object { -not ($_.type -eq $parts[0] -and $_.sourcePrefix -eq $parts[1]) })
        $rows = [System.Collections.Generic.List[object]]::new()
        foreach ($k in $kept) { $rows.Add($k) }
        $rows.Add(@{ type = $parts[0]; sourcePrefix = $parts[1]; targetPrefix = $parts[2] })
    }
    $null = Invoke-OneArr -Method Put -Path '/api/library/mappings' -Body $rows.ToArray()
    Say "Path mappings saved: $($rows.Count)"
}
if ($RootFrom -or $RootTo) {
    if (-not ($RootFrom -and $RootTo -and $Kind)) { throw '-RootFrom, -RootTo and -Kind must be given together.' }
    Say "Root folder change: $Kind entries '$RootFrom' -> '$RootTo'"
    $confirm = Read-Answer "Type MOVE to change the root folder of matching $Kind entries (files on disk are not moved)"
    if ($confirm -ne 'MOVE') { throw 'Root folder change not confirmed. Nothing was changed.' }
    $moved = Invoke-OneArr -Method Post -Path '/api/media/root-folder' -Body @{ mediaType = $Kind; fromRootFolder = $RootFrom; toRootFolder = $RootTo; ids = $null }
    Say "Root folder entries updated: $($moved.updated)"
}

# Library folders are added in Settings > Library. Without them there is nothing to scan.
$status = Invoke-OneArr -Method Get -Path '/api/library/status'
if (@($status.roots).Count -eq 0) { throw 'No library folders are configured. Add them in Settings > Library, then run this again.' }
Say "Library folders: $((@($status.roots) | ForEach-Object { "$($_.mediaType) $($_.path)" }) -join '; ')"

# 4. Snapshot the catalog before anything is linked.
function Get-Snapshot {
    $items = [System.Collections.Generic.List[object]]::new()
    foreach ($type in 'movie', 'series') {
        $list = Invoke-OneArr -Method Get -Path "/api/media?type=$type"
        foreach ($item in $list) {
            $items.Add([pscustomobject]@{ id = $item.id; type = $type; title = $item.title; year = $item.year; monitored = $item.monitored; hasFile = $item.hasFile; rootFolder = $item.rootFolder; legacyPath = $item.legacyPath })
        }
    }
    $wanted = Invoke-OneArr -Method Get -Path '/api/wanted'
    return [pscustomobject]@{ items = $items; wantedMovies = @($wanted.movies).Count; wantedEpisodes = @($wanted.episodes).Count }
}
$before = Get-Snapshot
$before | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $run 'catalog-before.json') -Encoding utf8
Say "Catalog: $(@($before.items | Where-Object type -eq 'movie').Count) movies, $(@($before.items | Where-Object type -eq 'series').Count) series, $(@($before.items | Where-Object hasFile).Count) with a file"
Say "Wanted before: $($before.wantedMovies) movies, $($before.wantedEpisodes) episodes"

# 5. Preview. Read-only for media: OneArr stores the reviewed plan only.
$preview = Invoke-OneArr -Method Post -Path '/api/library/preview'
$plan = $preview.plan
$plan | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $run 'plan.json') -Encoding utf8

$byId = @{}
foreach ($i in $before.items) { $byId[[int]$i.id] = $i }
$wouldAvailable = @($plan.links | Where-Object { $byId.ContainsKey([int]$_.mediaItemId) -and -not $byId[[int]$_.mediaItemId].hasFile } | ForEach-Object { [int]$_.mediaItemId } | Sort-Object -Unique)
$wouldMissing = @($plan.missing | Where-Object { $byId.ContainsKey([int]$_.mediaItemId) -and $byId[[int]$_.mediaItemId].hasFile } | ForEach-Object { [int]$_.mediaItemId } | Sort-Object -Unique)
$filesFound = ($plan.roots | Measure-Object -Property filesFound -Sum).Sum
$inaccessible = [int]$plan.filesUnderUnavailableRoots + @($plan.unknown).Count

Say ''
Say '=== Preview (nothing has been written) ==='
foreach ($r in $plan.roots) {
    $state = if ($r.state -eq 'Accessible') { if ($r.complete) { 'complete' } else { "PARTIAL: $($r.incompleteReason)" } } else { "UNAVAILABLE: $($r.message)" }
    Say "Folder $($r.path): $state, $($r.filesFound) media files"
}
Say "Catalog items checked: $($before.items.Count)"
Say "Files discovered: $filesFound"
Say "Confirmed matches (will link): $(@($plan.links).Count)  already linked: $($plan.alreadyLinked)"
Say "Ambiguous matches (not linked): $(@($plan.ambiguous).Count)"
Say "Unmatched files (not linked): $(@($plan.unmatched).Count)"
Say "Inaccessible or undecidable: $inaccessible"
Say "Items that would become Available: $($wouldAvailable.Count)"
Say "Items that would be marked Missing (had a file before): $($wouldMissing.Count)"
foreach ($w in $plan.warnings) { Say "Warning: $w" }
foreach ($a in @($plan.ambiguous | Select-Object -First 20)) { Say "  ambiguous: $($a.path) - $($a.reason)" }
foreach ($u in @($plan.unmatched | Select-Object -First 20)) { Say "  unmatched: $($u.path) - $($u.reason)" }
foreach ($m in @($plan.missing | Select-Object -First 20)) { Say "  missing: $($m.path)" }
$report | Set-Content -LiteralPath (Join-Path $run 'report.txt') -Encoding utf8

# 6. Hard stops. An incomplete scan or an ambiguous match is never applied silently.
if (-not $plan.complete) { Say 'Scan is incomplete. Fix the folders above and preview again. Nothing was applied.'; exit 2 }
if (@($plan.links).Count -eq 0 -and @($plan.missing).Count -eq 0) { Say 'Nothing to apply.'; exit 0 }
if ($wouldMissing.Count -gt 0) {
    $ok = Read-Answer "$($wouldMissing.Count) item(s) that had a file would be marked Missing and could be grabbed again. Type MISSING to accept"
    if ($ok -ne 'MISSING') { Say 'Not confirmed. Nothing was applied.'; exit 0 }
}
$ack = $false
if (@($plan.unmatched).Count -gt 0) {
    $ack = (Read-Answer "Type ACKNOWLEDGE to confirm the $(@($plan.unmatched).Count) unmatched file(s) are not catalog items") -eq 'ACKNOWLEDGE'
}

# 7. Explicit confirmation before any association is written.
$confirm = Read-Answer "Type APPLY to write $(@($plan.links).Count) association(s) and $(@($plan.missing).Count) missing mark(s) to OneArr's database. No media file changes."
if ($confirm -ne 'APPLY') { Say 'Not confirmed. Nothing was applied.'; exit 0 }

$result = Invoke-OneArr -Method Post -Path "/api/library/plans/$($preview.planId)/apply" -Body @{ excludePaths = @(); acknowledgeUnmatched = $ack }
Say ''
Say '=== Applied ==='
Say "Linked $($result.linked), relinked $($result.relinked), marked missing $($result.markedMissing), unknown $($result.markedUnknown), inaccessible $($result.markedInaccessible)"
Say "Catalog entries reviewed: $($result.reviewedItems); complete: $($result.complete)"
Say "Automatic grabbing: $($result.automation.reason) (not enabled by this script)"

# 8. Verify: what changed, and whether anything available is still listed as wanted.
$after = Get-Snapshot
$after | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $run 'catalog-after.json') -Encoding utf8
$afterById = @{}
foreach ($i in $after.items) { $afterById[[int]$i.id] = $i }
$becameAvailable = @($after.items | Where-Object { $_.hasFile -and $byId.ContainsKey([int]$_.id) -and -not $byId[[int]$_.id].hasFile }).Count
$becameMissing = @($after.items | Where-Object { -not $_.hasFile -and $byId.ContainsKey([int]$_.id) -and $byId[[int]$_.id].hasFile }).Count
$wantedAndAvailable = @($after.items | Where-Object { $_.hasFile -and $_.monitored -and $_.type -eq 'movie' }).Count
Say "Items now Available: $becameAvailable   items now Missing: $becameMissing"
Say "Wanted after: $($after.wantedMovies) movies, $($after.wantedEpisodes) episodes (was $($before.wantedMovies), $($before.wantedEpisodes))"
Say "Output folder: $run"
$report | Set-Content -LiteralPath (Join-Path $run 'report.txt') -Encoding utf8
