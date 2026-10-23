#!/usr/bin/env bash
# Runs the E03 scenarios (E03-A and E03-B by default) once per seed with `docker compose up`, then
# prints, per scenario, strategy and metric, the median and the min-max over the seeds.
#
#   ./repeat-e03.sh                          # seeds 42 1 2 3 4: about 20-25 minutes
#   E03_REPEAT_SEEDS="42 7" ./repeat-e03.sh  # other seeds
#   E03_SCENARIOS=b ./repeat-e03.sh          # one scenario only
#
# Every other E03_* / E03B_* variable is passed through to compose.yaml, as with `docker compose up`.
# Output (results/repeat/ is emptied first, so the aggregate covers exactly these seeds):
#         results/repeat/seed-N/e03-{a,b}/ (as a normal run), results/repeat/seed-N.log (compose output),
#         results/repeat/repeat.txt and repeat.json (the aggregate, also printed at the end).
# On macOS the script keeps the machine awake while it runs (caffeinate): Docker's VM pauses during
# idle sleep, which would distort the runs.
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ "$(uname)" == "Darwin" && -z "${E03_CAFFEINATED:-}" ]] && command -v caffeinate >/dev/null; then
  E03_CAFFEINATED=1 exec caffeinate -di "$0" "$@"
fi

COMPOSE=(docker compose -f "$DIR/compose.yaml")
read -r -a SEEDS <<<"${E03_REPEAT_SEEDS:-42 1 2 3 4}"
OUT="$DIR/results/repeat"
trap '"${COMPOSE[@]}" down --remove-orphans >/dev/null 2>&1' EXIT

rm -rf "${OUT:?}"
mkdir -p "$OUT"
"${COMPOSE[@]}" build -q dependency loadgen
for seed in "${SEEDS[@]}"; do
  echo "==> seed $seed ($(date +%H:%M:%S)), log: results/repeat/seed-$seed.log"
  if ! E03_SEED="$seed" E03_RESULTS_DIR="/results/repeat/seed-$seed" \
      "${COMPOSE[@]}" up --no-build --exit-code-from loadgen >"$OUT/seed-$seed.log" 2>&1; then
    echo "seed $seed failed; see results/repeat/seed-$seed.log" >&2
    exit 1
  fi
  "${COMPOSE[@]}" down --remove-orphans >/dev/null 2>&1
done

echo "==> aggregate ($(date +%H:%M:%S))"
"${COMPOSE[@]}" run --rm --no-deps -T loadgen aggregate /results/repeat
