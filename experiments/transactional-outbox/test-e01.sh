#!/usr/bin/env bash
# Runs the E01 xUnit suite inside Docker against the compose PostgreSQL and RabbitMQ,
# then removes the containers and volumes it created. No local .NET SDK required.
# Extra arguments are passed to `dotnet test`, e.g. --filter "FullyQualifiedName~A_B".
set -euo pipefail
COMPOSE=(docker compose -f "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/compose.yaml")
trap '"${COMPOSE[@]}" --profile test down -v --remove-orphans >/dev/null 2>&1' EXIT
"${COMPOSE[@]}" --profile test build -q tests
"${COMPOSE[@]}" run --rm tests "$@"
