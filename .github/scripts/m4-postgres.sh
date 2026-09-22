#!/usr/bin/env bash
set -euo pipefail

command="${1:-}"
container="${2:-foxdata-m4-postgres}"
image="${M4_POSTGRES_IMAGE:-postgres:18.6-bookworm}"
db_name="${M4_DB_NAME:-foxdata}"
db_user="${M4_DB_USER:-foxdata}"
db_password="${M4_DB_PASSWORD:-foxdata_m4}"

container_running() {
  [[ "$(docker inspect --format '{{.State.Running}}' "$container" 2>/dev/null || true)" == "true" ]]
}

final_server_ready() {
  container_running || return 1

  local pid1
  pid1="$(docker exec "$container" sh -c 'cat /proc/1/comm' 2>/dev/null || true)"
  [[ "$pid1" == "postgres" ]] || return 1

  docker exec "$container" \
    pg_isready \
      --host 127.0.0.1 \
      --port 5432 \
      --username "$db_user" \
      --dbname "$db_name" \
    >/dev/null 2>&1 || return 1

  local probe
  probe="$(
    docker exec \
      --env "PGPASSWORD=$db_password" \
      "$container" \
      psql \
        --host 127.0.0.1 \
        --port 5432 \
        --username "$db_user" \
        --dbname "$db_name" \
        --no-psqlrc \
        --tuples-only \
        --no-align \
        --set ON_ERROR_STOP=1 \
        --command 'SELECT 1;' \
      2>/dev/null
  )" || return 1

  [[ "$probe" == "1" ]]
}

wait_for_final_postgres() {
  local consecutive=0

  for attempt in $(seq 1 120); do
    if ! container_running; then
      echo "PostgreSQL container '$container' stopped before final readiness." >&2
      docker inspect "$container" >&2 2>/dev/null || true
      docker logs "$container" >&2 2>/dev/null || true
      return 1
    fi

    if final_server_ready; then
      consecutive=$((consecutive + 1))
      if (( consecutive >= 3 )); then
        echo "Final PostgreSQL server is stable over TCP (3 consecutive probes)."
        return 0
      fi
    else
      consecutive=0
    fi

    sleep 1
  done

  echo "Timed out waiting for the final PostgreSQL server." >&2
  echo "PID 1 command: $(docker exec "$container" sh -c 'cat /proc/1/comm' 2>/dev/null || echo unavailable)" >&2
  docker inspect "$container" >&2 2>/dev/null || true
  docker logs "$container" >&2 2>/dev/null || true
  return 1
}

start_postgres() {
  docker rm --force "$container" >/dev/null 2>&1 || true

  local pulled=false
  for attempt in 1 2 3 4 5; do
    if docker pull "$image" >/dev/null; then
      pulled=true
      break
    fi

    local delay=$(( attempt * 5 ))
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
    --env POSTGRES_DB="$db_name" \
    --env POSTGRES_USER="$db_user" \
    --env POSTGRES_PASSWORD="$db_password" \
    "$image" >/dev/null

  wait_for_final_postgres
}

stop_postgres() {
  docker rm --force "$container" >/dev/null 2>&1 || true
}

case "$command" in
  start)
    start_postgres
    ;;
  wait)
    wait_for_final_postgres
    ;;
  stop)
    stop_postgres
    ;;
  *)
    echo "usage: $0 <start|wait|stop> [container]" >&2
    exit 64
    ;;
esac
