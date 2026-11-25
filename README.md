# Home Automation

Home Automation provides a configurable, read-only dashboard for property monitoring, with separate pages for Growatt, Daikin ONECTA, eWeLink, and Smart Life / Tuya. ASP.NET Core serves the React application and can run a simulator for local development independently of connected browsers.

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
npm.cmd ci  # First setup or after package-lock.json changes
npm.cmd run dev
```

Wait for Vite to print its `Local: http://localhost:5173/` line, then open that URL and sign in as the provisioned viewer. The backend health endpoint and Vite run in separate processes. Keep both terminals open while developing. Vite proxies `/api`, `/health`, and `/hubs` to **http://localhost:5080**, including SignalR WebSockets.

With frontend dependencies restored, select **Home Automation (API + web)** in VS Code to run both debug sessions. Select **Home Automation API** or **Home Automation web** to debug either one independently. The web launch starts Vite through its task. Start the API separately when debugging only React. Use **Tasks: Run Task** for terminal-only startup. Backend debugging uses the C# debugger and web debugging uses VS Code's JavaScript debugger with Chrome. If Vite is already running, use the existing browser tab or stop its terminal before starting the web launch.

For terminal launches, press **Ctrl+C** in each terminal to stop both services. In VS Code, stop the debug sessions and use **Tasks: Terminate Task** if the Vite task is still running. Check the Vite task terminal for startup errors.

| Symptom | Check |
| --- | --- |
| Missing database setting | Set the named key in Development User Secrets or in the process environment. Recheck inherited overrides. |
| MySQL connection or access denied | Confirm the server, port, schema, and account with the database owner. Check `Database__SslMode` if the error reports TLS. |
| `/health/live` is 200 and `/health/ready` is 503 | Check MySQL connectivity and whether the migrations ran on the configured schema. The simulator waits for the schema and retries database failures. |
| `ERR_CONNECTION_REFUSED` at port 5173 | Start `npm.cmd run dev` from the frontend directory and check its terminal for the `Local:` line or a startup error. |
| Vite starts but API requests fail | Check http://localhost:5080/health/ready directly, then http://localhost:5173/health/ready through Vite. |
| Port 5080 or 5173 is occupied | Run `netstat -ano`, find the port's `LISTENING` entry, and identify its PID with `Get-Process -Id <PID>`. Reuse an existing Home Automation instance or stop the process from its own terminal or debugger. Vite exits when port 5173 is occupied. |
| Sign-in fails after readiness succeeds | Use the provisioned dashboard email and password. Inspect the bootstrap command result if the viewer is missing. |

## Optional Docker Compose deployment

1. Copy `.env.example` to `.env`. Set distinct strong values for the three MySQL passwords and set an email and strong password for the local viewer account. Use at least 16 letters, digits, underscores or hyphens for `MYSQL_RUNTIME_PASSWORD`. Use at least 14 characters with upper and lowercase letters, a digit, and punctuation for `BOOTSTRAP_PASSWORD`. Keep `.env` private.
2. Run the stack from the repository root.

```sh
docker compose up -d --build
docker compose ps -a
```

3. Open http://localhost:8080 and sign in with your viewer account. A new property opens with an empty Dashboard. The Devices page shows the simulator once it has completed a poll.

Compose binds the app and MySQL to loopback. A one-shot service applies the embedded, version-controlled SQL scripts using the migration account and provisions the viewer account. The app starts with a separate DML-only database account. The DB volume and application key volume persist across routine stops. The supplied deployment is for local monitoring.

Use `docker compose logs migrate app` to inspect startup and `docker compose down` to stop. MySQL initialization creates the database users on the first start of a volume. Changing the bootstrap password in `.env` does not reset an existing account. For a new viewer, set a different email and rerun `docker compose run --rm migrate`.

## Configurable monitoring dashboard

Apply migrations through `013_dashboard_measurement_colors.sql` with the migration account using `dotnet run --project src/backend/HomeAutomation.Api -- --migrate`. For the dashboard layout upgrade, the migration account needs `ALTER` on `dashboard_widgets` and `dashboard_values` in addition to its existing grants. Existing widgets start at one grid column and two measurement columns, and existing values keep automatic colors. Each property has a shared layout. Its members can read it, and members with the `Operator`, `Admin`, or `Owner` property role can save it. A bootstrapped `Viewer` account stays read-only.

If migrations 012 and 013 were executed as standalone SQL, the dashboard can report incomplete migration history even when the columns exist. Check the log for the missing records. Where both records are absent, first compare the live table definitions with the versioned migrations, including defaults, column types, and enforced checks. After confirming both definitions match, a database administrator can record the already-applied scripts with their runner checksums.

```sql
INSERT INTO homeautomation.schema_migrations (name, checksum) VALUES
  ('HomeAutomation.Infrastructure.012_dashboard_widget_layout.sql', '9BE1A3000EF9C38843030F4DE136D270383BDF70D239A00116E17A6B3E810B29'),
  ('HomeAutomation.Infrastructure.013_dashboard_measurement_colors.sql', 'BB275783DE97F51F5061BC470CE38B62AD656BC4FCEA929340B2B9B65787EBCD');
```

The dashboard checks the ledger on each request. Retry after recording the missing rows. The SQL above records migration history only.

The runtime account needs `DELETE` on `dashboard_groups` so a save can replace a property's layout in one transaction. After applying migration 011, run `SELECT CURRENT_USER();` through a runtime connection to verify the exact MySQL account and host. Then have the database administrator apply the table-specific grant, using that account and the configured schema. The Windows setup example uses the following grant.

```sql
GRANT DELETE ON homeautomation.dashboard_groups TO 'home_runtime'@'127.0.0.1';
```

InnoDB cascades removal to the matching widgets and values. The grant is scoped to the group table, and application membership checks still govern who can edit each property. If a save failed for missing storage permissions, apply the grant and retry **Save dashboard** in the open editor.

Select the property, open **Dashboard**, and choose **Configure dashboard**. Set each widget's width and measurement columns independently. Drag measurements by their handles or use the move buttons. Use automatic freshness colors or choose a fixed preset. Preview changes before saving. In monitoring mode, each measurement opens its own assigned provider page or, for a simulated source, its device detail. The dashboard remains read-only even when the destination offers device controls.

Readings come from saved Growatt, Daikin ONECTA, eWeLink and Smart Life / Tuya data. Simulator readings appear only when selected and are labeled. Automatic values are green for less than five minutes, blue from five minutes to under one hour, and gray from one hour onward. Age uses the recorded observation instant. The tooltip shows the original timestamp and provenance, and Detailed mode shows these details in the widget. A fixed color changes only the value, while a missing reading remains an em dash. Visible groups always show all their widgets. Widgets start expanded and retain **Show all** and **Collapse** controls when measurements span more than two rows.

Use **Compact** to switch all widgets to compact mode while preserving measurement visibility. **Detailed** switches all widgets to detailed mode and expands manually collapsed measurements. Each widget's view icon switches its mode independently. The shared layout stores the default display modes.

### Assign the initial owner of Countryside House

Migration 002 seeded Countryside House before a local account existed. The separate `--bootstrap-user` operation granted Viewer access. To assign ownership to the sole Viewer of the seeded property, first confirm that the backend database settings point to the intended Home Automation database. Then, from the repository root in a separate PowerShell session, enter the sole member's exact sign-in email and run the explicit administrative command.

```powershell
$env:PropertyAdmin__AccountEmail = Read-Host 'Sole member sign-in email'
try {
    dotnet run --project src/backend/HomeAutomation.Api -- --promote-initial-owner
    if ($LASTEXITCODE -ne 0) { throw 'Ownership promotion did not complete.' }
} finally {
    Remove-Item Env:PropertyAdmin__AccountEmail
}
```

The command checks that property 1 is the seeded `countryside` property and that the confirmed account is its sole Viewer. It updates that one membership. Running it again for the same sole Owner is safe. The command needs permission to read the property and account and update the property membership. Reload the dashboard after success to see **Configure dashboard**.

### Create a new property with an owner

Use `dotnet run --project src/backend/HomeAutomation.Api -- --create-property` in a separate administrative session. Set `PropertyAdmin__AccountEmail` to the existing creator account's sign-in email, `PropertyAdmin__SiteKey` to a unique lowercase key such as `city-home`, `PropertyAdmin__Name` to the display name, and `PropertyAdmin__TimeZone` to an IANA zone such as `Europe/Sofia`. The command inserts the property and its creator's Owner membership in one transaction. An existing site key causes the command to fail before any membership changes.

## Growatt read-only monitoring

Sign in and choose **Live Growatt** in the property sidebar to open the inverter detail page. Use the [Growatt cloud setup guide](docs/integrations/growatt.md) to check account discovery, confirm the inverter roles and configure read-only polling.

## Daikin ONECTA

Use the [Daikin ONECTA setup guide](docs/integrations/daikin-onecta.md) to register the developer app, apply migration 003, and connect an account from the property sidebar. Physical writes are disabled by default.

### Local HTTPS for ONECTA on Windows

The HTTPS development origin is `https://ha-dev.britishlab.org:5443`. Confirm the Windows hosts file resolves `ha-dev.britishlab.org` to `127.0.0.1`. The following one-time setup uses [mkcert](https://github.com/FiloSottile/mkcert). Put its official portable Windows executable on your PATH or invoke it by absolute path. It can run without a system-wide installation.

```powershell
$tls = Join-Path $env:LOCALAPPDATA 'HomeAutomation/tls'
$env:CAROOT = Join-Path $tls 'ca'
New-Item -ItemType Directory -Path $tls,$env:CAROOT -Force | Out-Null
mkcert -cert-file (Join-Path $tls 'ha-dev.britishlab.org.pem') -key-file (Join-Path $tls 'ha-dev.britishlab.org-key.pem') ha-dev.britishlab.org
```

Trust the public CA used for this development certificate in the current Windows user's **Trusted Root Certification Authorities** store. This gives the current user's applications trust in certificates signed by that CA. Windows may prompt for confirmation. Keep both the host key and the CA key private under LocalAppData.

```powershell
certutil.exe -user -addstore Root (Join-Path $env:CAROOT 'rootCA.pem')
```

From the repository root, save only the new callback and start the backend in Development mode.

```powershell
dotnet user-secrets set 'Daikin:RedirectUri' 'https://ha-dev.britishlab.org:5443/api/v1/daikin/callback' --project src/backend/HomeAutomation.Api
dotnet run --project src/backend/HomeAutomation.Api
```

In another terminal, from `src/frontend/home-automation-web`, run:

```powershell
npm.cmd run dev:https
```

Open [https://ha-dev.britishlab.org:5443/](https://ha-dev.britishlab.org:5443/) and sign in on that origin. Vite listens on `127.0.0.1:5443`, ends browser TLS there, and forwards `/api`, `/health`, and SignalR over the local loopback hop to port 5080. The backend recognizes the forwarded scheme only for that named Development origin from its trusted loopback peer. The ordinary `npm.cmd run dev` workflow remains available on port 5173.

Set the existing Daikin app's redirect URI to `https://ha-dev.britishlab.org:5443/api/v1/daikin/callback`. Start and finish Daikin consent from the HTTPS origin in the same browser. See the [ONECTA 403 diagnosis](docs/integrations/daikin-onecta.md#diagnosing-cloudfront-403) if the provider rejects the authorization request.

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
