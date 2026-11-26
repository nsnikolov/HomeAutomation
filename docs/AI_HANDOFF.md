# Home Automation development handoff

Updated 2026-11-26. [AGENTS.md](../AGENTS.md) contains the shared development rules. This file records shipped behavior and the latest verified state. The [roadmap](architecture/08-implementation-roadmap.md) records intended future work.

## Current implementation

- The foundation ships an authenticated React application, property and room inventory, typed device capabilities, an ASP.NET Core API, MySQL telemetry persistence, health endpoints, and SignalR. The simulator covers parallel inverters, a shared battery, an AC, a metering switch, and a sensor. The backend ingests while browsers are closed.
- Cookie authentication and server-side property membership protect API operations and SignalR. A bootstrapped account starts as Viewer. Owner, Admin, and Operator can edit a property's shared dashboard. The guarded initial-owner command targets a verified sole Viewer membership. The property creation command provisions the property and its initial Owner together. See [property setup](../README.md#assign-the-initial-owner-of-countryside-house).
- The monitoring Dashboard supports grouped widgets with cross-provider measurements, configurable widget widths and measurement columns, pointer and keyboard measurement ordering, automatic freshness or fixed colors, observation provenance, and links to authorized device views. The dashboard itself is read-only for equipment. Layout edits are stored in MySQL.
- Widget Compact/Detailed mode and measurement expansion are independent browser preferences scoped to account, property, and widget. A new widget starts Compact and expanded. Global Compact keeps expansion, and global Detailed expands every widget. A browser reload restores preferences from localStorage. See [widget persistence verification](prompts/0050-persist-dashboard-widget-view.md).
- Device-list ordering is saved per account, property, and page. Tuya devices can be hidden or restored as presentation preferences. History shows dated sensor measurements and leaves gaps for missing observations. See [device ordering and history](prompts/0019-history-and-dashboard-ux.md).
- Milestone 7A adds provider-neutral history on the existing raw store. Migration 017 adds UTC hourly rollups per physical series and a materialized-range state. A background worker recomputes the last 72 hours every 15 minutes, and an explicit dry-run-first CLI backfills older history. `GET /api/v1/properties/{id}/history` serves raw, hourly and local-day buckets with coverage, quality and energy provenance; `history/series` lists the series. Measured counter energy and estimated power energy are never combined. Charts are Milestone 7C. See [historical telemetry](architecture/10-historical-telemetry.md).
- Milestone 7B adds logical devices (migration 018). A logical channel is assigned to a physical device, optionally one endpoint, for half-open intervals. Owner and Admin can replace a device at an exact past or present instant, with capability checks, conflict detection under a property row lock, and an audit trail. Logical history at raw, hourly and daily resolution uses only observations inside each assignment, so counter deltas and held values never cross devices. Dashboard sources `logical:{id}:{channel}:{Metric}` follow the active device and never show a reading taken before its assignment. An explicit review converts selected physical bindings. The Devices page shows logical devices, previous hardware, and a confirmed Replace device flow. `--initialize-logical-devices` creates identities for existing devices in a dry-run-first, idempotent step. See [logical devices](architecture/11-logical-devices.md).
- Milestone 7C adds the **History** page with property-local ranges (24 hours, 7 days, 30 days, 12 months, custom) at hourly, daily or monthly resolution, or as individual readings. It shows min/max bands, measured and estimated energy kept apart, relay ON time, quality and coverage, exact replacement markers, and comparison of up to four logical devices. See [history charts](architecture/12-history-charts.md).
- Milestone 7D adds optional background eWeLink electrical collection.
  - **Root cause:** electrical history needed an open eWeLink page, because UIID 5/32 meters report only inside `uiActive` windows and only the browser's viewing lease sent them.
  - **Mechanism:** with `Ewelink:ContinuousElectricalCollection=true` (default off), the existing socket worker's single activation loop opens one 60-second window per eligible meter every `Ewelink:ElectricalCollectionSeconds` (default 900). Starts are staggered after a restart.
  - **Budget:** the interval stretches to fit the request budget. Electrical activations also pause each UTC day at 1,406 requests.
  - **Failures:** a CoolKit 504 backs off only that meter.
  - **Samples:** they come only from device frames, use receipt-time provenance, and never come from timers or cached HTTP values. The 2-minute hold is unchanged, so coverage stays honestly partial.
  - **Unchanged:** no migration, no new worker, no vault schema change beyond two optional fields.
  - See [eWeLink electrical collection](architecture/13-ewelink-electrical-collection.md).
- Settings stages provider settings in a global or property profile. Property Owner/Admin may edit their own scope. Global access requires an explicitly designated Identity account. Settings reads ordinary identifiers and polling intervals. Genuine secrets are write-only and stored with an external AES-256-GCM key. Since Milestone 6B, each provider can load a complete imported vault snapshot at startup when explicitly selected. Existing deployments use Legacy by default and OAuth grants stay in their existing encrypted stores. See the [configuration and cutover guide](architecture/09-configuration-vault.md).

| Integration | Shipped behavior and evidence |
| --- | --- |
| [Growatt](integrations/growatt.md) | Read-only V4 cloud monitoring for the two SPF 5000 ES inverters and nominated shared battery source. Live polls verified separate PV, AC input, battery power and daily PV, output and battery-discharge counters. Daily counters stay per inverter. |
| [Daikin ONECTA](integrations/daikin-onecta.md) | OAuth with PKCE, live discovery and telemetry for air conditioners and read-only Altherma. Eligible ACs support guarded manual controls and debounced account-wide readback. Historical supervised power and target changes were reported for a dx4. A later read-only discovery verified dx23 and dx4 target metadata. |
| [eWeLink](integrations/ewelink-cloud.md) | OAuth, inventory, Force Refresh, relay and sensor monitoring. HTTP reconciliation complements optional authenticated WebSocket updates and opt-in UIID 32 electrical reporting. Live inventory confirmed PSF-X67 electrical values and THR320D temperature/humidity. A [live trace](prompts/0035-ewelink-electrical-timestamps.md) captured timestamp-less electrical frames. The receive-time fallback was verified with fixtures. Optional 7D background windows collect electrical history without a browser (fixture-verified only). Relay writes are separately gated for eligible PSF-X67 devices. |
| [Smart Life / Tuya](integrations/tuya-cloud.md) | Signed regional Cloud requests, discovery, capability-based status, sensor history, and local card preferences. Verified mappings use device-supplied data-point metadata, scale and units. Read-only. |

### Recent implementation commits

| Commit | Change |
| --- | --- |
| Milestone 7D | Optional background eWeLink electrical collection, budget daily share, per-meter back-off and receipt-time duplicate fix. See [prompt 0059](prompts/0059-continuous-ewelink-power-telemetry.md). |
| `33a2c5c` | History page with logical-device charts, energy provenance, quality, replacement markers and comparisons. See [prompt 0058](prompts/0058-historical-charts-and-energy.md). |
| `35838fa` | Milestone 7B logical devices, replacement, continuous logical history, logical dashboard sources and the Devices replacement UI. See [prompt 0057](prompts/0057-logical-devices-and-replacement.md). |
| `754615e` | Milestone 7A hourly rollups, local-day history, energy provenance, history API and backfill CLI. See [prompt 0056](prompts/0056-historical-telemetry-aggregation.md). |
| `e8d66b2` / `c044da8` | Provider startup diagnostics, explicit vault import and restart cutover. |
| `bd5fbd8` | Persist Dashboard widget mode and expansion in localStorage. |
| `99dc88b` | Distinguish missing Dashboard migration records from MySQL read failures. |
| `1742015` | Configurable widget layout, colors, measurement ordering and tile navigation. |
| `5e33da3` / `bdbbb70` | Guarded property ownership administration and the configurable Dashboard. |
| `1d91ed9` / `2a9ed5d` / `efc66e5` | Growatt daily counters, Daikin dx23 temperature eligibility, and eWeLink receipt-time provenance. |

## Architecture

- [Backend boundaries](architecture/02-backend-architecture.md) and the [modular monolith ADR](architecture/adr/0001-modular-monolith.md) describe Domain and Application contracts, Infrastructure adapters and persistence, and API composition in [Program.cs](../src/backend/HomeAutomation.Api/Program.cs). The [capability and binding ADR](architecture/adr/0007-device-capabilities-and-provider-bindings.md) describes device identity and provider boundaries.
- [Frontend](architecture/03-frontend-architecture.md) uses React, TypeScript, Vite, TanStack Query, and SignalR to invalidate property-scoped reads. Dashboard presentation is in [Dashboard.tsx](../src/frontend/home-automation-web/src/features/Dashboard.tsx) and [DashboardWidgets.tsx](../src/frontend/home-automation-web/src/features/DashboardWidgets.tsx). Layout and source queries are in [MonitoringDashboardStore.cs](../src/backend/HomeAutomation.Infrastructure/MySql/MonitoringDashboardStore.cs).
- Dapper and MySqlConnector store normalized telemetry in MySQL. Preserve source observation time, receipt time, provenance, unit and device identity. Use SignalR to notify authorized browsers of persisted updates. See the [data model](architecture/04-data-model.md) and [telemetry ADR](architecture/adr/0006-telemetry-storage.md).
- The [migration runner](../src/backend/HomeAutomation.Infrastructure/MySql/Migrations.cs) applies embedded scripts from [database/migrations](../database/migrations/), serializes execution, and records successful names and SHA-256 checksums in `schema_migrations`. Current version is [018](../database/migrations/018_logical_devices.sql). Settings staging requires 014/015. Provider import and vault activation require 014–016. Historical rollups and hourly or daily history require 017. Logical devices require 017 and 018. Readiness and raw history require neither. Keep the runtime account separate from the migration account.

## Windows development and deployment

Use PowerShell, .NET 10, Node.js 24 with `npm.cmd`, and the dedicated database setup in [README](../README.md#develop-on-windows-without-docker). Local MySQL 8.0.19 or later in the 8.0 series is supported. MySQL 8.4 is the deployment target.

```powershell
# Repository root, API terminal
dotnet run --project src/backend/HomeAutomation.Api

# Separate terminal, from the repository root
Set-Location src/frontend/home-automation-web
npm.cmd ci
npm.cmd run dev
```

Open `http://localhost:5173`. Vite proxies to the API on `http://localhost:5080`. To restart, stop the relevant terminal process with Ctrl+C and rerun its command. VS Code's **Home Automation (API + web)** compound starts both debuggers. See [launch settings](../.vscode/launch.json) and [tasks](../.vscode/tasks.json).

The API writes structured JSON to its stdout in the launch terminal or debugger. Vite writes startup and proxy output in its own terminal. For Compose, inspect `docker compose logs migrate app`. Check `/health/live` and `/health/ready`; provider and Dashboard readiness can have additional prerequisites.

For OAuth, use the [loopback HTTPS setup](../README.md#local-https-for-onecta-on-windows) and `npm.cmd run dev:https` on the configured HTTPS origin. The callback requires the registered origin and the browser that started consent. Compose is optional. Its documented local bindings are loopback, and the [security and deployment guide](architecture/07-security-deployment.md) describes HTTPS, LAN/VPN options and private MySQL networking.

For an approved database setup, use the [dedicated migration account instructions](../README.md#apply-migrations-and-provision-a-viewer) with these API arguments from the repository root.

| Command suffix after `dotnet run --project src/backend/HomeAutomation.Api --` | Purpose |
| --- | --- |
| `--migrate` | Apply and record pending schema changes. |
| `--migrate --bootstrap-user` | Apply migrations and provision the initial Viewer when needed. |
| `--promote-initial-owner` | Guarded one-time ownership recovery with administrative credentials. |
| `--create-property` | Create a property and its initial Owner together. |

Use the [property instructions](../README.md#create-a-new-property-with-an-owner) for the required settings. Never run migrations or change permissions on the live schema without explicit approval. Store runtime secrets outside the repository.

## Operating limits and safety

| Adapter | Default cadence and constraints |
| --- | --- |
| Growatt | One batched latest-data request per five minutes for the inverter pair. Preserve provider throttling and backoff. AC input behind the transfer switch does not independently identify utility import or prove grid availability. Avoid double-counting the shared battery. |
| Daikin | 15-minute poll. Rolling 200-call budget with a 40-call reserve from background use. Accepted PATCHes share a 30-second debounce for readback. The checked-in Development configuration enables the manual-write flag by default. Production defaults to disabled. Account, role, freshness and capability checks still apply. |
| eWeLink | Five-minute HTTP synchronization. The default CoolKit budget is 50,000 calls per region per month and the documented IP ceiling is 300 calls per five minutes. Respect paced requests, cooldowns and shared WebSocket budget. Manual relay writes default off and require stable-device opt-in. Protected inverter relays require per-command confirmation. THR320D stays read-only. |
| Tuya | 30-minute minimum default with bounded discovery and history requests. Project plan, quota and trial expiry are account-dependent. Use the regional project and authorized Smart Life linkage. |

Keep manual commands on provider pages and behind explicit authorization. Record the requested state and confirm it through observed readback. Preserve uncertain-outcome blocking and avoid automatic command replay. Never send real equipment commands from automated tests. Growatt, Tuya and Altherma remain read-only. Autonomous rule execution and local-agent control are future roadmap work.

## Current status and next steps

- Branch `main`. Milestone 6C builds on `c044da8`. The operator reported completing the cutover for Growatt, Tuya, Daikin and eWeLink. The running backend was left in place while startup diagnostics were added.
- Use [read-only provider verification](../README.md#verify-provider-startup-on-windows) to match startup event 6100 to the listener PID and start time. Existing logs may lack current events until the next planned restart. The restart script now reports redacted startup error categories and refuses listeners it cannot identify as this checkout.
- Daikin, eWeLink and Tuya use global account profiles. Growatt can also use a property profile whose SiteKey resolves to that property. Workers get the complete singleton snapshot chosen at startup. A selected vault failure stops startup. Settings changes apply on restart.
- Daikin continues to use its original external `Daikin:TokenKey` and eWeLink its original ASP.NET Data Protection key ring. Roll back the source selector without reverting the database so both providers can read the most recent grant envelopes. The vault does not import or copy grant data. Tuya's short-lived access token remains in its guarded process cache.
- The last read-only live Dashboard audit confirmed that the 012/013 schema changes were present but their two ledger records were missing. The [record-only repair](../README.md#configurable-monitoring-dashboard) restored HTTP 200 on isolated MySQL. The live schema was left untouched by the audit, and there is no recorded confirmation that the operator subsequently applied the ledger repair. If the 503 persists, verify the current ledger and schema before using the documented repair. See the [diagnosis](prompts/0049-dashboard-schema-503.md).
- The last documented eWeLink inventory included six devices, while CoolKit reported seven things. Pagination now follows a safe advancing cursor after a short page. The live availability of the missing thermostat is still unverified. Use normal polling and sanitized diagnostics to investigate within quota.
- Milestones 7A, 7B and 7C are implemented but **not deployed**. Until migrations 017 and 018 are applied, the History page shows "History is not available yet" and other pages are unaffected. Migrations 017 and 018 have not been applied to the live database, no backfill or initialization has run there, and the running backend has not been restarted. Follow the approved [deployment sequence](../README.md#logical-devices-and-device-replacement): backup, migrate, restart, 7A backfill dry run then execute, logical initialization dry run then execute, then validation. No grant change is needed.
- Milestone 7D is implemented but **not deployed or enabled**.
  - **Deployment:** it needs no migration beyond 010. After an approved restart with `Ewelink:ContinuousElectricalCollection=true`, follow the [24-hour no-browser verification](integrations/ewelink-cloud.md#background-electrical-collection-milestone-7d). For a vault-backed eWeLink profile, the setting requires a dry-run-first re-import.
  - **Open questions, answerable only on real hardware:**
    - whether a meter reports when a window opens, or only on later changes, which matters for steady loads;
    - whether CoolKit counts WebSocket updates against its quota;
    - whether non-PSF-X67 UIID 5/32 models accept `uiActive`.
- Growatt inverter replacement needs a separate configuration workflow because the Growatt identity guard rejects bindings that differ from the configured serials. It was intentionally not weakened.
- Deferred from 7B: creating logical devices, channels and corrections from the UI (the API supports them), automatic identities for devices discovered after initialization, and the Growatt hardware workflow. Deferred from 7C: zoom and brush interactions, export, cross-metric comparisons, and charts for physical series without a logical device.
- Use the [roadmap](architecture/08-implementation-roadmap.md) for new work when requested. Do not treat its future architecture as deployed.

## Verification baseline

- **Milestone 7D**:
  - **Backend:** the Release build passed with zero warnings. Tests reported 287 passes and 11 opt-in skips with the history, logical and dashboard fixtures on disposable MySQL 8.0.46, twice in a row. Without fixtures, they reported 272 passes and 26 skips.
  - **New unit tests (21):**
    - scheduling with no viewer, restart stagger, and shared tab activations;
    - per-meter 504 back-off with jitter, the unacknowledged streak, and budget stretching for 5, 10, 20 and 50 meters;
    - UIID eligibility, receipt-time repeat versus genuine-repeat handling, and invalid values;
    - loading an older vault profile;
    - the collector loop under acceptance, 504, rejection, throttling, lost acknowledgements, idling and cancellation;
    - summaries that contain no device IDs.
  - **End-to-end MySQL test:** the service path with no viewer sent only `uiActive` and charged the budget. It persisted receipt-timed samples, including a genuine repeat, and skipped offline and out-of-scope meters. Hourly coverage was exactly two 2-minute holds, with partial estimated energy. The daily share blocked activations while HTTP could still reserve requests.
  - **Frontend:** the production build passed, and Vitest reported 83 passes, adding the collection-status card test.
  - **Not touched:** the live database, the running backend and the providers. Live meter behavior during background windows is unverified; see the [verification procedure](integrations/ewelink-cloud.md#background-electrical-collection-milestone-7d).
- **Milestone 7C** — Frontend production build passed; History is a separate 34 kB lazy chunk and there was no chunk-size warning. Vitest reported 82 passes, including 12 history-model tests (DST and offset zones, custom boundaries, weighted months and summaries, gaps, raw breaks, markers) and 12 History page tests on mocked APIs. Backend: the Release build passed with zero warnings, and 265 tests passed with 11 opt-in skips. That covered the additive `metrics` and `maximumHoldSeconds` fields on disposable MySQL 8.0.46. No migration was added, and nothing touched the live database, backend or providers.
- **Milestone 7B** — Release build passed with zero warnings. Backend tests reported 265 passes and 11 skips from other opt-in fixtures, twice in a row with the history, logical-device and dashboard MySQL suites running in parallel on disposable MySQL 8.0.46. Coverage includes 12 replacement-rule and segment-planner tests, 6 logical MySQL tests and 10 new HTTP tests. The MySQL tests used DML-only runtime accounts and covered: upgrading a populated 017 schema; idempotent initialization; replacing a Tuya sensor with an eWeLink one at a microsecond instant; humidity going unsupported; electrical counters on two meters across a 25-hour DST day; permissions, invalid times, stale and concurrent replacements, rollback, corrections and archiving; and dashboard conversion and follow-the-replacement behaviour. The rollup lock is now scoped to the schema. The frontend production build and 57 Vitest tests passed. A CLI dry run of the initialization wrote nothing. The live database, running backend and providers were not touched.
- **Milestone 7A** — Release build passed with zero warnings. Backend tests reported 236 passes and 12 skips from other opt-in MySQL fixtures. They include 34 pure aggregation tests (DST, +05:30/+05:45 days, counters, power, relays, hourly + raw-partial equivalence), 7 history MySQL tests on disposable MySQL 8.0.46, 20 HTTP history tests, and 3 worker tests. The history MySQL tests use a runtime account limited to `SELECT, INSERT, UPDATE`, and cover upgrading a populated 016 schema with unchanged checksums, first-write-wins, late data, a failed transaction, and an interrupted and resumed backfill. A CLI smoke dry run against the disposable schema wrote nothing. The live database, running backend and providers were not touched. The frontend was unchanged.
- **Milestone 6C** — Release build passed with zero warnings. Backend tests reported 172 passes and 12 opt-in database skips. Windows PowerShell fixtures covered early exit, redaction and unknown/stale log evidence. An isolated launch verified an Invalid Vault summary. The current listener returned HTTP 200 and the verifier reported Unknown for all four providers because there were no matching startup events.
- **Milestone 6B** — Release backend and production frontend builds passed. xUnit reported 174 passed and nine unrelated opt-in skips. Disposable MySQL 8.0.46 verified migration 016, no-write preview, explicit replacement and idempotent import, scope binding, startup of all four vault-backed singleton options, local OAuth envelope continuity, and rollback after synthetic refresh. The CLI smoke check verified redacted dry-run output and fail-closed startup for an absent selected profile. All 51 Vitest tests passed.
- **Milestone 6A.1** — Release backend and production frontend builds passed. xUnit reported 167 passed and nine unrelated opt-in skips. Disposable MySQL 8.0.46 verified upgrading a populated 014 schema, plaintext identifier reads and scoped edits, authenticated recovery and rollback with a missing key, revision conflicts, redacted audit, genuine-secret rotation, and the existing Dashboard regression. The conversion CLI reported zero on rerun. Vitest reported 51 passed.
- **Milestone 6A** — Both Release backend and production frontend builds passed. xUnit reported 167 passed and nine unrelated opt-in MySQL tests skipped. Disposable MySQL 8.0.46 verified migration 014, settings access and transport, optimistic concurrency, redacted audit, nonce reservation, and key rotation with scoped runtime grants. Vitest reported 49 passed.
- **Frontend at `bd5fbd8`** — Production build and all 47 Vitest tests passed. A headless Chrome fixture verified refresh, property switching, and browser close/reopen. The tests also cover navigation, telemetry updates and corrupt or unavailable storage. See [verification record](prompts/0050-persist-dashboard-widget-view.md).
- **Backend at `99dc88b`** — Release build passed. xUnit reported 163 passed and nine skipped in other opt-in MySQL fixtures. An isolated MySQL 8.0.46 regression verified the dashboard ledger repair and safe read errors. Live investigation was read-only. See [verification record](prompts/0049-dashboard-schema-503.md).
- **Repeat locally** — Use `dotnet build HomeAutomation.slnx -c Release` and `dotnet test HomeAutomation.slnx -c Release --no-build` from the root. Use `npm.cmd run build` and `npm.cmd test` in the frontend directory after restoring dependencies. Opt-in database tests use dedicated fixture schemas. Inspect [tests](../tests/) for the required environment variable names. The regular test suite does not need provider credentials.
- The integration table links prior live observations. Mocked command and provider tests do not establish current account access, current flags, or live switching results.

## Switching agents

1. Start Codex or Claude Code with the same repository root and branch. Use the handoff plus `git status --short` and `git log -8 --oneline` to recover the latest task state.
2. Codex loads [AGENTS.md](../AGENTS.md) through project instructions and follows its instruction to read this file. Claude Code loads [CLAUDE.md](../CLAUDE.md), which imports both files. In Claude Code, `/memory` lists the active instruction files.
3. If work remains, inspect the working-tree diff and its [original prompt](prompts/README.md) before editing. Capture new verification and blockers here before handing the task back.
