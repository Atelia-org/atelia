#!/usr/bin/env bash
# Synthetic instances only. Never imports or opens the operator's Galatea state.
set -Eeuo pipefail
umask 077

lab_configuration=Release
lab_live=false
for lab_argument in "$@"; do
    case "$lab_argument" in
        Debug|Release) lab_configuration="$lab_argument" ;;
        --live) lab_live=true ;;
        *) printf 'Usage: %s [Debug|Release] [--live]\n' "$0" >&2; exit 2 ;;
    esac
done
lab_repo="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd -- "$lab_repo"
if "$lab_live"; then
    if [[ "${ATELIA_CODEX_SUBSCRIPTION_LIVE_AUTH_FILE:-}" != /* \
        || ! -r "${ATELIA_CODEX_SUBSCRIPTION_LIVE_AUTH_FILE:-}" ]]; then
        printf 'Live requires an explicit readable absolute ATELIA_CODEX_SUBSCRIPTION_LIVE_AUTH_FILE.\n' >&2
        exit 2
    fi
fi
lab_results="$(mktemp -d "${TMPDIR:-/tmp}/atelia-galatea-lab-results.XXXXXX")"
trap 'printf "Lab results retained: %s\n" "$lab_results"' EXIT

env -u ATELIA_CODEX_SUBSCRIPTION_ACCOUNT_FINGERPRINT \
    -u ATELIA_CODEX_SUBSCRIPTION_AUTH_FILE \
    -u ATELIA_RUN_GALATEA_LAB_LIVE \
    ATELIA_DEBUG_FILE_LEVEL=Error ATELIA_DEBUG_CONSOLE_LEVEL=Error \
    dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj \
        -c "$lab_configuration" -m:1 -nr:false \
        --filter 'Category=GalateaLab|FullyQualifiedName~GalateaScenarioLabTests' \
        --logger 'trx;LogFileName=offline.trx' --results-directory "$lab_results" \
        --verbosity quiet

python3 - "$lab_results/offline.trx" <<'PY'
import sys
import xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
counts = root.find('.//{*}Counters').attrib
assert int(counts['executed']) > 0 and int(counts['failed']) == 0, counts
print('Offline lab executed:', counts['executed'])
PY

if "$lab_live"; then
    ATELIA_RUN_GALATEA_LAB_LIVE=1 \
    ATELIA_GALATEA_LAB_LIVE_REPORT="$lab_results/live.jsonl" \
    ATELIA_DEBUG_FILE_LEVEL=Error ATELIA_DEBUG_CONSOLE_LEVEL=Error \
        dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj \
            -c "$lab_configuration" --no-build --no-restore -m:1 -nr:false \
            --filter 'FullyQualifiedName~LiveE2E_LunaTwoTurnsAcrossColdReopen' \
            --logger 'trx;LogFileName=live.trx' --results-directory "$lab_results" \
            --verbosity quiet
    python3 - "$lab_results/live.jsonl" <<'PY'
import json
import sys
with open(sys.argv[1], encoding='utf-8') as report:
    rows = [json.loads(line) for line in report]
summary = rows[-1]
assert summary['stage'] == 'summary' and summary['success'], summary
assert summary['completedTurns'] == 2 and summary['Calls'] == 2, summary
print('Live lab completed: 2 turns across cold reopen')
PY
fi
