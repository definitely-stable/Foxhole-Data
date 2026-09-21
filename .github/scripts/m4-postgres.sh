#!/usr/bin/env bash
set -euo pipefail

command="${1:-}"
container="${2:-foxdata-m4-postgres}"
image="${M4_POSTGRES_IMAGE:-postgres:18.6-bookworm}"

start_postgres() {
  docker rm --force "$container" >/dev/null 2>&1 || true

  local pulled=false
  for attempt in 1 2 3 4 5; do
    if docker pull "$image" >/dev/null; then
      pulled=true
      break
    fi

    delay=$(( attempt * 5 ))
    echo "PostgreSQL image pull attempt $attempt failed; retrying in ${delay}s." >&2
    sleep "$delay"
  done

  if [[ "$pulled" != "true" ]]; then
    echo "Could not pull PostgreSQL image '$image' after five attempts." >&2
    exit 1
  fi

  docker run \
    --detach \
    --pull never \
    --name "$container" \
    --publish 127.0.0.1:5432:5432 \
    --env POSTGRES_DB=foxdata \
    --env POSTGRES_USER=foxdata \
    --env POSTGRES_PASSWORD=foxdata_m4 \
    "$image" >/dev/null

  for attempt in $(seq 1 60); do
    if docker exec "$container" pg_isready -U foxdata -d foxdata >/dev/null 2>&1; then
      exit 0
    fi
    sleep 1
  done

  docker logs "$container" >&2 || true
  exit 1
}

stop_postgres() {
  docker rm --force "$container" >/dev/null 2>&1 || true
}

case "$command" in
  start)
    start_postgres
    ;;
  stop)
    stop_postgres
    ;;
  *)
    echo "usage: $0 <start|stop> [container]" >&2
    exit 64
    ;;
esac
