#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
umask 077
mkdir -p .local/backups
backup=".local/backups/accounts-$(date -u +%Y%m%dT%H%M%SZ).dump"
docker compose exec -T db pg_dump -U digitchess -d digitchess -Fc > "$backup"
printf 'Backup saved: %s\n' "$backup"
