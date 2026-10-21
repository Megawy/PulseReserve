#!/usr/bin/env bash
# Developer helper for the local Docker stack (compose.dev.yaml). Usage: scripts/dev.sh <command> [args]
set -euo pipefail
cd "$(dirname "$0")/.."
C=(docker compose -f compose.dev.yaml)

case "${1:-help}" in
  setup)   # create .env with a random database password (never overwrites an existing .env)
    if [ -f .env ]; then echo ".env already exists; leaving it untouched."; else
      pw=$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')
      sed "s/change-me-dev-only/$pw/" .env.example > .env; echo "Created .env"; fi ;;
  up)      "${C[@]}" up -d --build --wait; "${C[@]}" ps ;;
  stop)    "${C[@]}" stop ;;                       # keeps containers and the database volume
  status)  "${C[@]}" ps ;;
  logs)    shift; "${C[@]}" logs -f --tail=100 "$@" ;;
  restart) shift; "${C[@]}" restart "$@" ;;
  rebuild) shift; "${C[@]}" up -d --build "$@" ;;  # after changing a Dockerfile / SDK version
  seed)    node scripts/dev-seed.mjs ;;
  migrate) "${C[@]}" restart api ;;                # the dev API applies pending migrations on start
  test-backend)  dotnet test PulseReserve.sln ;;
  test-frontend) (cd frontend/pulsereserve-web && npm run lint && npm run test:ci && npm run build) ;;
  psql)    "${C[@]}" exec postgres sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB"' ;;
  *) echo "commands: setup up stop status logs [svc] restart <svc> rebuild [svc] seed migrate test-backend test-frontend psql"
     echo "Data is kept across stop/up. Deleting data (docker compose down -v) is deliberately not offered here." ;;
esac
