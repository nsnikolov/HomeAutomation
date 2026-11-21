# Home Automation

Milestone 1 is a read-only monitoring application for a simulated countryside house. It monitors two parallel Growatt inverters, a shared battery, a Daikin air conditioner, a Sonoff metering switch, and a room sensor. ASP.NET Core serves the React dashboard and runs the simulator independently of connected browsers.

## Develop on Windows without Docker

Use PowerShell, the .NET 10 SDK, Node.js 24 with `npm.cmd`, and an existing MySQL server. VS Code is optional for running the application. For local development use MySQL 8.0.19 or later in the 8.0 series, or MySQL 8.4. MySQL 8.4 remains the deployment target.

### Get a dedicated schema and scoped accounts

Confirm the server host and its TCP port with the database owner or in your existing Workbench connection. A service name such as `MySQL80` identifies a Windows service, not its listener port. An existing authorized connection can run `SELECT VERSION(), @@port;`. The example below uses 3306. Substitute the confirmed port.

If you already have a dedicated Home Automation schema and scoped accounts, use those. On a shared server, ask its database owner to provision them explicitly. The [setup SQL example](database/windows-setup.example.sql) shows the permissions for a fresh `homeautomation` schema. The example is manual and separate from app migrations. The owner should verify the names are unused and execute each statement in order, stopping if any fails. Use the actual local account host and save generated passwords in a private credential store.

### Configure the API

Run from the repository root. Development User Secrets are local to your Windows profile and stored outside Git in plaintext. Use them for local development credentials. PowerShell prompts keep the values out of command history. [ASP.NET Core User Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets?view=aspnetcore-10.0)

```powershell
$api = 'src/backend/HomeAutomation.Api'
@{
    'Database:Host' = '127.0.0.1'
    'Database:Port' = 3306
    'Database:Name' = 'homeautomation'
    'Database:User' = 'home_runtime'
    'Database:Password' = [System.Net.NetworkCredential]::new('', (Read-Host 'Runtime MySQL password' -AsSecureString)).Password
} | ConvertTo-Json | dotnet user-secrets set --project $api
```

All five settings are required. Process environment variables such as `Database__Port` override User Secrets. Keep any existing overrides aligned with this schema. The checked-in launch profile selects `Development` and port 5080. If you use `--no-launch-profile`, set the environment and URL explicitly. The Compose `.env` file is read by Compose.

### Apply migrations and provision a viewer

With the schema ready and migrations approved, use a dedicated migration account for the one-shot command. Enter a new dashboard password of at least 14 characters with uppercase, lowercase, a digit, and punctuation. This password is separate from the MySQL account password. Use a separate PowerShell session for this step.

```powershell
$api = 'src/backend/HomeAutomation.Api'
$env:Database__User = 'home_migrator'
$env:Database__Password = [System.Net.NetworkCredential]::new('', (Read-Host 'Migration MySQL password' -AsSecureString)).Password
$env:Bootstrap__Email = Read-Host 'Dashboard viewer email'
$env:Bootstrap__Password = [System.Net.NetworkCredential]::new('', (Read-Host 'Dashboard viewer password' -AsSecureString)).Password
try {
    dotnet run --project $api -- --migrate --bootstrap-user
    if ($LASTEXITCODE -ne 0) { throw 'Home Automation setup failed. See the error above.' }
} finally {
    Remove-Item Env:Database__User, Env:Database__Password, Env:Bootstrap__Email, Env:Bootstrap__Password
}
```

The API checks schema readiness at runtime. DDL is applied only via `--migrate`. The runner serializes application, records script checksums, and skips applied versions. Migration 001 creates InnoDB tables with conditional creation and checked relationships. Migration 002 seeds the demo property with duplicate-key handling that preserves existing rows. Both target the explicitly selected schema. MySQL DDL can commit between statements. If a run fails, resolve the reported error and verify the dedicated schema before retrying. Keep previously applied migration files intact. Running bootstrap again for the same email preserves its password. [MySQL implicit commits](https://dev.mysql.com/doc/refman/8.4/en/implicit-commit.html)

The static compatibility review found two MySQL 8.0 patch-level constraints. MySQL enforces the schema's `CHECK` constraints from 8.0.16, and the runtime telemetry writer's upsert row aliases require 8.0.19. The seed query's duplicate-key syntax is documented for 8.0 and 8.4. [MySQL 8.0 CHECK constraints](https://dev.mysql.com/doc/refman/8.0/en/create-table-check-constraints.html) · [MySQL 8.0 upserts](https://dev.mysql.com/doc/refman/8.0/en/insert-on-duplicate.html) · [MySQL 8.4 upserts](https://dev.mysql.com/doc/refman/8.4/en/insert-on-duplicate.html)

### Run and debug

In one terminal at the repository root, start the API with the runtime account configuration.

```powershell
dotnet run --project src/backend/HomeAutomation.Api
```

In a second terminal, start the React app.

```powershell
Set-Location src/frontend/home-automation-web
npm.cmd ci
npm.cmd run dev
```

Open **http://localhost:5173** and sign in as the provisioned viewer. Vite proxies `/api`, `/health`, and `/hubs` to **http://localhost:5080**, including SignalR WebSockets.

With frontend dependencies restored, select **Home Automation (API + web)** in VS Code to run both debug sessions. Select **Home Automation API** or **Home Automation web** to debug either one independently. The web launch starts Vite through its task. Start the API separately when debugging only React. Use **Tasks: Run Task** for terminal-only startup. Backend debugging uses the C# debugger and web debugging uses VS Code's JavaScript debugger with Chrome. If Vite is already running, use the existing browser tab or stop its terminal before starting the web launch.

| Symptom | Check |
| --- | --- |
| Missing database setting | Set the named key in Development User Secrets or in the process environment. Recheck inherited overrides. |
| MySQL connection or access denied | Confirm the server, port, schema, and account with the database owner. Check `Database__SslMode` if the error reports TLS. |
| `/health/live` is 200 and `/health/ready` is 503 | Check MySQL connectivity and whether the migrations ran on the configured schema. The simulator waits for the schema and retries database failures. |
| Vite proxy error, port conflict, or blank dashboard | Confirm both processes are listening on their documented ports. Check http://localhost:5080/health/ready, then reload http://localhost:5173. Vite exits if port 5173 is occupied. |
| Sign-in fails after readiness succeeds | Use the provisioned dashboard email and password. Inspect the bootstrap command result if the viewer is missing. |

## Optional Docker Compose deployment

1. Copy `.env.example` to `.env`. Set distinct strong values for the three MySQL passwords and set an email and strong password for the local viewer account. Use at least 16 letters, digits, underscores or hyphens for `MYSQL_RUNTIME_PASSWORD`. Use at least 14 characters with upper and lowercase letters, a digit, and punctuation for `BOOTSTRAP_PASSWORD`. Keep `.env` private.
2. Run the stack from the repository root.

```sh
docker compose up -d --build
docker compose ps -a
```

3. Open http://localhost:8080 and sign in with your viewer account. Give the simulator up to one poll interval to populate the dashboard.

Compose binds the app and MySQL to loopback. A one-shot service applies the embedded, version-controlled SQL scripts using the migration account and provisions the viewer account. The app starts with a separate DML-only database account. The DB volume and application key volume persist across routine stops. The supplied deployment is for local monitoring.

Use `docker compose logs migrate app` to inspect startup and `docker compose down` to stop. MySQL initialization creates the database users on the first start of a volume. Changing the bootstrap password in `.env` does not reset an existing account. For a new viewer, set a different email and rerun `docker compose run --rm migrate`.

## Verify changes

```powershell
dotnet build HomeAutomation.slnx -c Release
dotnet test HomeAutomation.slnx -c Release --no-build
Set-Location src/frontend/home-automation-web
npm.cmd ci
npm.cmd run build
npm.cmd test
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
