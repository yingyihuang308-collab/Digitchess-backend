#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
mkdir -p .local
chmod 700 .local
if [ ! -f .local/jwt-private.pem ]; then
  openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out .local/jwt-private.pem 2>/dev/null
fi
chmod 600 .local/jwt-private.pem
if [ ! -f .local/local.env ]; then
  password=$(openssl rand -hex 24)
  umask 077
  cat > .local/local.env <<ENV
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS=http://127.0.0.1:5080
export ConnectionStrings__Database='Host=127.0.0.1;Port=55432;Database=digitchess;Username=digitchess;Password=$password'
export Auth__PrivateKeyPath='$PWD/.local/jwt-private.pem'
ENV
  printf '%s\n' "$password" > .local/db-password
fi
PG_BIN=${PG_BIN:-/opt/homebrew/opt/postgresql@17/bin}
if [ ! -d .local/pgdata ]; then
  "$PG_BIN/initdb" -D .local/pgdata -U digitchess --pwfile=.local/db-password --auth=scram-sha-256 --encoding=UTF8 --locale=C > .local/initdb.log
fi
if ! "$PG_BIN/pg_ctl" -D .local/pgdata status >/dev/null 2>&1; then
  "$PG_BIN/pg_ctl" -D .local/pgdata -l "$PWD/.local/postgres.log" -o "-p 55432 -h 127.0.0.1 -k $PWD/.local" start
fi
export PGPASSWORD=$(cat .local/db-password)
if ! "$PG_BIN/psql" -h 127.0.0.1 -p 55432 -U digitchess -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='digitchess'" | grep -q 1; then
  "$PG_BIN/createdb" -h 127.0.0.1 -p 55432 -U digitchess digitchess
fi
printf '%s\n' 'Local PostgreSQL ready on 127.0.0.1:55432. Secrets remain in .local/.'
