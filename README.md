# Home Automation

Milestone 1 is a read-only monitoring application for a simulated countryside house. It monitors two parallel Growatt inverters, a shared battery, a Daikin air conditioner, a Sonoff metering switch, and a room sensor. ASP.NET Core serves the React dashboard and runs the simulator independently of connected browsers.

## Start with Docker Compose

1. Copy `.env.example` to `.env`. Set distinct strong values for the three MySQL passwords and set an email and strong password for the local viewer account. Use at least 16 letters, digits, underscores or hyphens for `MYSQL_RUNTIME_PASSWORD`. Use at least 14 characters with upper and lowercase letters, a digit, and punctuation for `BOOTSTRAP_PASSWORD`. Keep `.env` private.
2. Run the stack from the repository root.

```sh
docker compose up -d --build
docker compose ps -a
```

3. Open http://localhost:8080 and sign in with your viewer account. Give the simulator up to one poll interval to populate the dashboard.

Compose binds the app and MySQL to loopback. A one-shot service applies the embedded, version-controlled SQL scripts using the migration account and provisions the viewer account. The app starts with a separate DML-only database account. The DB volume and application key volume persist across routine stops. The supplied deployment is for local monitoring.

Use `docker compose logs migrate app` to inspect startup and `docker compose down` to stop. MySQL initialization creates the database users on the first start of a volume. Changing the bootstrap password in `.env` does not reset an existing account. For a new viewer, set a different email and rerun `docker compose run --rm migrate`.

## Develop and debug

Install the .NET 10 SDK and Node.js 24 or another supported Node version of at least 22.12. For local debugging, start only the database and migration job with `docker compose up --build migrate`. You can also use an isolated MySQL 8.4 instance with a pre-created schema and separate migration and runtime users.

Set `Database__Password` in your local environment to the runtime account password before running the API or launching VS Code. For a different instance, set the matching host, port, database, and user. To apply migrations and provision an account independently of Compose, use the migration account and set `Bootstrap__Email` and `Bootstrap__Password` in your private environment before running the setup command.

```sh
dotnet restore HomeAutomation.slnx
dotnet run --project src/backend/HomeAutomation.Api -- --migrate --bootstrap-user
```

Start the API with the runtime account again.

```sh
dotnet run --project src/backend/HomeAutomation.Api --no-launch-profile -- --urls http://localhost:5080
```

In a separate terminal, start the frontend.

```sh
cd src/frontend/home-automation-web
npm ci
npm run dev
```

Open http://localhost:5173. Vite proxies API and SignalR requests to port 5080. VS Code includes **Home Automation API** and **Home Automation web** launch entries. Start Vite before launching the browser debugger and make sure VS Code inherits the runtime database password. The API entry uses the C# debugger.

| Local setting | Purpose |
| --- | --- |
| `Database__Host`, `Database__Port`, `Database__Name`, `Database__User`, `Database__Password` | MySQL host, port, database, user and required secret. Local defaults use localhost on port 3307, schema `homeautomation`, and user `home_runtime`. |
| `Database__SslMode` | MySqlConnector TLS mode, default `Preferred`. Configure strict TLS before using a remote database. |
| `Bootstrap__Email`, `Bootstrap__Password` | Viewer account provisioning inputs for `--bootstrap-user`. |
| `Simulator__Enabled`, `Simulator__PollSeconds`, `Simulator__FreshnessSeconds` | Simulation enablement, poll interval and maximum sample age. Defaults are true, 10 and 45. Keep freshness at least twice the poll interval. |
| `Simulator__Scenario` | `Demonstration` cycles through normal, offline, fault and stale behavior. Use `Normal`, `Outage`, `Stale`, `Offline`, or `Fault` to pin a case. |
| `DataProtection__KeyDirectory` | Optional directory for persistent session keys. Compose manages it in a named volume. |

Compose maps the `SIMULATOR_*` settings from `.env` to the corresponding backend options. Health endpoints are `/health/live` and `/health/ready`. After sign-in, `/api/v1/system/health` also reports simulator status and last successful ingestion.

## Verify changes

```sh
dotnet build HomeAutomation.slnx
dotnet test HomeAutomation.slnx
cd src/frontend/home-automation-web
npm ci
npm run build
npm test
```

The xUnit API tests use a test host and synthetic property memberships. The unit and frontend tests cover freshness, device capabilities, parallel-inverter aggregation, simulator scenarios, and the read-only dashboard. For a manual integration check, sign in and wait for multiple samples on the Energy page. Close all browser tabs, wait two sample intervals, reopen the page and verify that the history includes those intervals.

## Repository map

- `src/backend/` holds the API, Application, Domain, and Infrastructure projects.
- `src/frontend/home-automation-web/` holds the React application and Vitest tests.
- `tests/` holds xUnit unit and API integration tests.
- `database/migrations/` holds numbered SQL. The runner serializes application, records checksums, and recognizes applied versions.
- `docker/` packages the frontend into the API image and provisions the limited runtime database account.
- [Architecture](docs/architecture/01-system-overview.md) and [prompt history](docs/prompts/README.md) record the design and its implementation requests.

Milestone 1 uses a seeded simulator property with property-scoped viewer accounts, raw telemetry history, and device-status alerts. The architecture roadmap covers account management UI, rollups and retention, live provider adapters, device commands, automation execution, and the future local agent.
