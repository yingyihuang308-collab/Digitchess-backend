#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
. .local/local.env
dotnet run --project src/Backend -- --migrate
exec dotnet run --no-build --project src/Backend
