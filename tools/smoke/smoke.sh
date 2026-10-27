#!/usr/bin/env bash
# End-to-end smoke test against local fake upstream services (tools/fake-services). Requires: dotnet 10 SDK, python3, curl, jq.
# Uses a scratch directory (SCRATCH, default: mktemp). Never touches real media, real trackers or production services.
# Credentials used here are fake fixture values.
set -uo pipefail

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
[ -d "$HOME/.dotnet" ] && export PATH="$HOME/.dotnet:$PATH"
SCRATCH="${SCRATCH:-$(mktemp -d)}"
API="http://127.0.0.1:8790"
FAKE_LOG="$SCRATCH/fake-requests.jsonl"
FAIL=0
TOKEN=""

check() {
  if eval "$2"; then echo "PASS  $1"; else echo "FAIL  $1"; FAIL=1; fi
}
ag() { curl -fsS -H "Authorization: Bearer $TOKEN" "$@"; }                          # authenticated GET
ap() { local url="$1"; shift; curl -fsS -X POST -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' "$url" -d "$1"; }
code() { curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' "$@"; }
postcode() { curl -s -o /dev/null -w '%{http_code}' -X POST -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' "$1" -d "$2"; }

mkdir -p "$SCRATCH/torrents/onearr-movies" "$SCRATCH/media/movies" "$SCRATCH/config"
echo "synthetic video bytes" > "$SCRATCH/torrents/onearr-movies/The.Matrix.1999.2160p.BluRay.x265-GRP.mkv"

FAKE_CONTENT_BASE="$SCRATCH/torrents" FAKE_LOG="$FAKE_LOG" python3 "$REPO/tools/fake-services/fake_services.py" >"$SCRATCH/fake.out" 2>&1 &
FAKE_PID=$!
ONEARR_CONFIG_DIR="$SCRATCH/config" ONEARR_PORT=8790 ONEARR_TMDB_BASE_URL="http://127.0.0.1:9001/3/" \
  dotnet run --project "$REPO/src/OneArr.Api" --no-build >"$SCRATCH/api.out" 2>&1 &
API_PID=$!
trap 'kill $API_PID $FAKE_PID 2>/dev/null; wait 2>/dev/null' EXIT

for _ in $(seq 1 120); do curl -fsS "$API/health" >/dev/null 2>&1 && break; sleep 1; done

check "health endpoint responds" "curl -fsS $API/health | jq -e '.status==\"ok\"' >/dev/null"
check "SPA served at root" "curl -fsS $API/ | grep -q '<div id=\"root\">'"
check "API requires auth" "[ \"\$(curl -s -o /dev/null -w '%{http_code}' $API/api/dashboard)\" = 401 ]"

TOKEN=$(curl -fsS -X POST $API/api/auth/setup -H 'Content-Type: application/json' -d '{"password":"correct horse battery"}' | jq -r .token)
check "first-run setup issues token" "[ -n \"\$TOKEN\" ] && [ \"\$TOKEN\" != null ]"
check "second setup is refused" "[ \"\$(curl -s -o /dev/null -w '%{http_code}' -X POST $API/api/auth/setup -H 'Content-Type: application/json' -d '{\"password\":\"another long password\"}')\" = 409 ]"
check "wrong password rejected" "[ \"\$(curl -s -o /dev/null -w '%{http_code}' -X POST $API/api/auth/login -H 'Content-Type: application/json' -d '{\"password\":\"nope nope nope\"}')\" = 401 ]"

check "settings saved (fixture TMDb key, roots, dry-run)" "[ \"\$(postcode $API/api/settings '{}')\" != 500 ] && curl -fsS -X PUT -H \"Authorization: Bearer \$TOKEN\" -H 'Content-Type: application/json' $API/api/settings -d '{\"tmdbApiKey\":\"0123456789abcdef0123456789abcdef\",\"torrentRoot\":\"$SCRATCH/torrents\",\"mediaRoot\":\"$SCRATCH/media\",\"dryRunImport\":true}' >/dev/null"
ap $API/api/indexers '{"name":"fixture-torznab","baseUrl":"http://127.0.0.1:9002/","apiKey":"SUPERSECRETKEY","enabled":true}' >/dev/null
ap $API/api/download-clients '{"name":"qbit","baseUrl":"http://127.0.0.1:9003","username":"admin","password":"QBPASSWORD","movieCategory":"onearr-movies","tvCategory":"onearr-tv","enabled":true}' >/dev/null
check "indexer and client secrets never returned" "! ag $API/api/indexers | grep -q SUPERSECRETKEY && ! ag $API/api/download-clients | grep -q QBPASSWORD"
check "TMDb search via fixture server" "ag '$API/api/metadata/search?type=movie&q=matrix' | jq -e '.[0].providerId==\"603\"' >/dev/null"

ADD=$(ap $API/api/media "{\"type\":\"movie\",\"providerId\":\"603\",\"qualityProfileId\":1,\"rootFolder\":\"$SCRATCH/media/movies\",\"monitored\":true}")
MID=$(echo "$ADD" | jq -r .item.id)
check "movie added" "[ \"$MID\" != null ] && echo '$ADD' | jq -e '.created==true' >/dev/null"
DUP=$(ap $API/api/media "{\"type\":\"movie\",\"providerId\":\"603\",\"qualityProfileId\":1,\"rootFolder\":\"$SCRATCH/media/movies\",\"monitored\":true}")
check "re-add is deduplicated" "echo '$DUP' | jq -e '.created==false' >/dev/null"

SEARCH=$(ap $API/api/search "{\"mediaItemId\":$MID}")
check "indexer results parsed with decisions" "echo '$SEARCH' | jq -e '.results | length >= 2' >/dev/null"
check "rejected releases carry reasons" "echo '$SEARCH' | jq -e '[.results[] | select(.accepted==false) | .reasons | length] | add > 0' >/dev/null"
LINK=$(echo "$SEARCH" | jq -r '[.results[] | select(.accepted==true)][0].link')
HASH=$(echo "$SEARCH" | jq -r '[.results[] | select(.accepted==true)][0].infoHash')
TITLE=$(echo "$SEARCH" | jq -r '[.results[] | select(.accepted==true)][0].title')
check "server refuses to grab a rejected release" "[ \"\$(postcode $API/api/grab '{\"mediaItemId\":'$MID',\"title\":\"Other.Movie.1999.1080p.BluRay\",\"link\":\"magnet:?xt=urn:btih:1111111111111111111111111111111111111111\"}')\" = 400 ]"

check "manual grab sent to qBittorrent" "[ \"\$(postcode $API/api/grab \"{\\\"mediaItemId\\\":$MID,\\\"title\\\":\\\"$TITLE\\\",\\\"link\\\":\\\"$LINK\\\",\\\"infoHash\\\":\\\"$HASH\\\"}\")\" = 201 ]"
check "grab recorded with hash and category" "ag $API/api/queue | jq -e '.[0].category==\"onearr-movies\" and .[0].hash!=null' >/dev/null"
check "duplicate grab refused" "[ \"\$(postcode $API/api/grab \"{\\\"mediaItemId\\\":$MID,\\\"title\\\":\\\"$TITLE\\\",\\\"link\\\":\\\"$LINK\\\",\\\"infoHash\\\":\\\"$HASH\\\"}\")\" = 400 ]"

ap $API/api/queue/sync '' >/dev/null
check "queue sync reads real qBittorrent state" "ag $API/api/queue | jq -e '.[0].status==\"Completed\"' >/dev/null"

DRY=$(ap "$API/api/import?dryRun=true" '')
check "dry run plans without writing media" "echo '$DRY' | jq -e '.dryRun==true' >/dev/null && [ -z \"\$(ls -A $SCRATCH/media/movies)\" ]"

JOBID=$(ag $API/api/queue | jq -r '.[0].id')
PREVIEWID=$(ap $API/api/queue/$JOBID/import-preview '' | jq -r '.previewId')
BULK="{\"confirmed\":true,\"approvals\":[{\"jobId\":$JOBID,\"previewId\":$PREVIEWID}]}"
ap "$API/api/import?dryRun=false" "$BULK" >/dev/null
check "real import hardlinks or copies into media root" "[ -f '$SCRATCH/media/movies/The Matrix (1999)/The.Matrix.1999.2160p.BluRay.x265-GRP.mkv' ]"
check "seeding source retained" "[ -f '$SCRATCH/torrents/onearr-movies/The.Matrix.1999.2160p.BluRay.x265-GRP.mkv' ]"
check "job marked imported" "ag $API/api/queue | jq -e '.[0].status==\"Imported\"' >/dev/null"
ap "$API/api/import?dryRun=false" "$BULK" >/dev/null
check "re-running import is idempotent" "[ \"\$(ls -A '$SCRATCH/media/movies/The Matrix (1999)' | wc -l)\" = 1 ]"

PREVIEW=$(ap $API/api/migration/preview '{"kind":"radarr","baseUrl":"http://127.0.0.1:9003","apiKey":"fixture-key"}')
check "migration preview reads legacy catalog" "echo '$PREVIEW' | jq -e '.readOnly==true and (.items | length)==1 and (.errors | length)==1' >/dev/null"
check "legacy reader issued only GET requests" "[ -n \"\$(grep '/api/v3' $FAKE_LOG)\" ] && [ -z \"\$(grep '/api/v3' $FAKE_LOG | grep -v '\"method\": \"GET\"')\" ]"

check "dashboard reflects real catalog" "ag $API/api/dashboard | jq -e '.movies==1' >/dev/null"
check "no qBittorrent delete endpoint called" "! grep -qi 'delete' $FAKE_LOG"

echo "scratch dir: $SCRATCH"
if [ "$FAIL" -eq 0 ]; then echo "SMOKE: ALL CHECKS PASSED"; else echo "SMOKE: FAILURES (see $SCRATCH/api.out)"; fi
exit $FAIL
