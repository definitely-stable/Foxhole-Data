#!/usr/bin/env bash
set -euo pipefail

command="${1:-}"
container="${2:-foxdata-m4-postgres}"
checkpoint_dir="${3:-m4-ci/checkpoint}"

db_name="${M4_DB_NAME:-foxdata}"
db_user="${M4_DB_USER:-foxdata}"
db_password="${M4_DB_PASSWORD:-foxdata_m4}"
db_host="${M4_DB_HOST:-127.0.0.1}"
db_port="${M4_DB_PORT:-5432}"

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

dump_path="$checkpoint_dir/checkpoint.dump"
tmp_path="$checkpoint_dir/checkpoint.dump.tmp"
sha_path="$checkpoint_dir/checkpoint.dump.sha256"

usage() {
  echo "usage: $0 <create|verify|restore> [container] [checkpoint-dir]" >&2
  exit 64
}

remote_path() {
  printf '/tmp/m4-checkpoint-%s-%s.dump' "$$" "$RANDOM"
}

validate_dump_file() {
  local source_path="$1"
  local remote
  remote="$(remote_path)"

  docker cp "$source_path" "$container:$remote"

  local status=0
  docker exec "$container" pg_restore --list "$remote" >/dev/null || status=$?
  docker exec "$container" rm -f "$remote" >/dev/null 2>&1 || true

  if (( status != 0 )); then
    echo "Checkpoint dump failed pg_restore --list validation." >&2
    return "$status"
  fi
}

verify_checkpoint() {
  test -s "$dump_path"
  test -s "$sha_path"

  (
    cd "$checkpoint_dir"
    sha256sum --check checkpoint.dump.sha256
  )

  validate_dump_file "$dump_path"
}

create_checkpoint() {
  bash "$script_dir/m4-postgres.sh" wait "$container"

  mkdir -p "$checkpoint_dir"
  rm -f "$tmp_path" "$dump_path" "$sha_path"

  docker exec \
    --env "PGPASSWORD=$db_password" \
    "$container" \
    pg_dump \
      --format=custom \
      --no-owner \
      --no-privileges \
      --host "$db_host" \
      --port "$db_port" \
      --username "$db_user" \
      --dbname "$db_name" \
    > "$tmp_path"

  test -s "$tmp_path"
  validate_dump_file "$tmp_path"

  mv "$tmp_path" "$dump_path"

  (
    cd "$checkpoint_dir"
    sha256sum checkpoint.dump > checkpoint.dump.sha256
  )

  verify_checkpoint
}

restore_checkpoint() {
  verify_checkpoint
  bash "$script_dir/m4-postgres.sh" wait "$container"

  local remote
  remote="$(remote_path)"
  docker cp "$dump_path" "$container:$remote"

  local status=0
  docker exec \
    --env "PGPASSWORD=$db_password" \
    "$container" \
    pg_restore \
      --clean \
      --if-exists \
      --no-owner \
      --no-privileges \
      --host "$db_host" \
      --port "$db_port" \
      --username "$db_user" \
      --dbname "$db_name" \
      "$remote" || status=$?

  docker exec "$container" rm -f "$remote" >/dev/null 2>&1 || true
  return "$status"
}

case "$command" in
  create)
    create_checkpoint
    ;;
  verify)
    verify_checkpoint
    ;;
  restore)
    restore_checkpoint
    ;;
  *)
    usage
    ;;
esac
