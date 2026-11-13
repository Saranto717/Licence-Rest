#!/usr/bin/env bash
set -euo pipefail

app_password="$(cat "$CLOUD_APP_PASSWORD_FILE")"

psql \
  --set=ON_ERROR_STOP=1 \
  --set=app_user="$CLOUD_APP_USER" \
  --set=app_password="$app_password" \
  --set=database_name="$POSTGRES_DB" \
  --username="$POSTGRES_USER" \
  --dbname="$POSTGRES_DB" <<'SQL'
CREATE ROLE :"app_user" LOGIN PASSWORD :'app_password';
GRANT CONNECT, TEMPORARY ON DATABASE :"database_name" TO :"app_user";
GRANT USAGE, CREATE ON SCHEMA public TO :"app_user";
SQL

unset app_password

