#!/usr/bin/env bash
# Runs the E03 xUnit suite inside Docker (no local .NET SDK needed), then removes what it created.
# Extra arguments are passed to `dotnet test`, e.g. --filter "FullyQualifiedName~Backoff".
set -euo pipefail
COMPOSE=(docker compose -f "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/compose.yaml")
trap '"${COMPOSE[@]}" --profile test down --remove-orphans >/dev/null 2>&1' EXIT
"${COMPOSE[@]}" --profile test build -q tests
"${COMPOSE[@]}" run --rm tests "$@"
