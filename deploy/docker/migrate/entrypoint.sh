#!/bin/sh
set -eu

connection=""
expect_connection_value=0

for argument in "$@"; do
  if [ "$expect_connection_value" -eq 1 ]; then
    connection="$argument"
    expect_connection_value=0
    continue
  fi

  case "$argument" in
    --connection)
      expect_connection_value=1
      ;;
    --connection=*)
      connection="${argument#--connection=}"
      ;;
  esac
done

if [ "$expect_connection_value" -eq 1 ]; then
  echo "--connection requires a non-empty value" >&2
  exit 64
fi

if [ -n "$connection" ]; then
  export FOXDATA_DESIGN_CONNECTION="$connection"
elif [ -z "${FOXDATA_DESIGN_CONNECTION:-}" ]; then
  echo "Migration bundle requires --connection or FOXDATA_DESIGN_CONNECTION" >&2
  exit 64
fi

exec /app/foxdata-migrate "$@"
