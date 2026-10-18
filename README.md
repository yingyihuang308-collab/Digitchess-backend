# DigitChess Backend

A standalone .NET 10 / PostgreSQL 17 backend for accounts, rooms, and WebSocket message forwarding. It contains no board state, move validation, GameState implementation, or code copied from the private project. The Unity changes for the two connection options remain in the private Assets directory of the parent project. Publish only this directory when publishing the backend.

## Running on the Current Development Machine

.NET SDK 10 and Homebrew PostgreSQL 17 are installed. The project-specific database is stored in `.local/pgdata`, listens only on 127.0.0.1:55432, and uses SCRAM password authentication. It is not configured to start automatically at login. Actual secrets are kept in the ignored `.local` directory and are not included in this document.

The backend runs at http://127.0.0.1:5080. Check its readiness from another terminal:

```sh
curl -f http://127.0.0.1:5080/health/ready
```

If the service is already running on port 5080, do not start another instance. Press Ctrl+C to stop a backend running in the foreground. Stop the database separately:

```sh
/opt/homebrew/opt/postgresql@17/bin/pg_ctl -D .local/pgdata -m fast stop
```

`init-local.sh` is idempotent: it does not overwrite an existing database, password, or private key. Set `PG_BIN` if PostgreSQL is installed somewhere other than the default location. Do not delete `.local` to resolve startup errors; it contains the account database and secrets.

## Running on Other Machines

Install the .NET 10 SDK, PostgreSQL 17, and OpenSSL. On macOS with Homebrew, run `brew install postgresql@17`. On Linux, install the official PostgreSQL packages for your distribution and set `PG_BIN`, or run the database with Docker.

To use Docker for the database instead:

```sh
cp .env.example .env
# Set POSTGRES_PASSWORD in .env to a randomly generated password.
docker compose -f compose.local.yaml up -d db
```

Both the Docker database and the native database use port 55432, so choose one. Their data volumes are independent. When using Docker, generate a local RSA private key and configure `ConnectionStrings__Database`, `Auth__PrivateKeyPath`, `ASPNETCORE_ENVIRONMENT=Development`, and `ASPNETCORE_URLS=http://127.0.0.1:5080`. Do not run the native database initialization script as well. Then run:

```sh
dotnet restore src/Backend
dotnet run --project src/Backend -- --migrate
dotnet run --no-build --project src/Backend
```

Run migrations explicitly in every environment. To create a migration after changing the schema:

```sh
dotnet tool install --tool-path .local/tools dotnet-ef --version 10.0.12
.local/tools/dotnet-ef migrations add DescriptiveMigrationName --project src/Backend
```

Commit migration source files to the repository. Do not commit actual private keys, database files, passwords, or backups.

## Two-Client Integration Tests

Start the backend in a separate terminal with a 5-second reconnection grace period for testing. The production default is 45 seconds.

```sh
. .local/local.env
export Rooms__ReconnectSeconds=5
dotnet run --project src/Backend
```

Then run:

```sh
dotnet run --project tests/Integration
```

The tests create random accounts and rooms to validate the actual database, HTTP API, and WebSocket connections. They disconnect their own connections and close their own rooms. Run them only against a local test service. When the local private key is available, the tests use it to generate a correctly signed but expired test JWT to verify expiration checks. They do not print the private key.

After the tests finish, restart both the backend and PostgreSQL, then run:

```sh
python3 scripts/probe-persistence.py
```

To test concurrent refresh requests separately, run `python3 scripts/probe-refresh-concurrency.py`.

The persistence probe uses the random test account in `.local/persistence-probe.json` to check that its fixed player ID, nickname, and password login still work after a restart. It is not intended for production accounts.

## Documentation

- [Protocol and Data Behavior](docs/PROTOCOL.md)
- [Unity Integration and Connection Switching](docs/UNITY.md)
- [Oracle Deployment, Backups, and Rollback](docs/DEPLOYMENT.md)
- [Verification Results and Limitations](docs/VERIFICATION.md)

The first version runs as a single instance and stores rooms in memory, so rooms must be recreated after a restart. Nicknames can be changed but cannot be used as account identities. Password recovery, email verification, third-party login, account linking between connection options, and shared match records are not implemented. The host validates game rules, so host cheating remains possible. Switching between the self-hosted server and Unity Relay requires a new room; it does not seamlessly migrate an ongoing match.
