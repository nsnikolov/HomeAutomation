# Simulator removal and cleanup

Normal operation now uses only provider data from Growatt, Daikin ONECTA, eWeLink and Smart Life / Tuya. The demonstration simulator is an opt-in tool for isolated development and tests. Stored simulator data is hidden while the simulator is off, and an explicit command deletes it. No migration was added.

## What depended on the simulator

| Area | Before | Now |
| --- | --- | --- |
| Configuration | `Simulator:Enabled` was `true` in `appsettings.json` and in the `SimulatorOptions` default. | `false` in both. Nothing turns it on automatically, including when no provider is configured. |
| `TelemetryWorker` | Registered six demo devices in the `countryside` property (the seeded Countryside House) every start. It then wrote observations every 10 s and broadcast `stateChanged` over SignalR. | Returns immediately unless enabled: no devices, observations, broadcasts or polling. |
| `/state`, `/devices`, `/devices/{id}` | Only devices without a real-provider binding, and only `simulator` readings. These fed the Devices page cards and the Energy page. | Devices bound to a real provider, with the latest reading per endpoint and metric from real series. Quality uses each source's 7A hold. Simulator devices appear only while it is enabled. Missing readings stay missing. |
| `/history` (Energy charts) | `simulator` series only. | One provider series per endpoint or installation metric (the most recently updated), never merged. |
| `/system/health` `pollSeconds` | The simulator's 10 s cadence, used by Energy for gap breaks. | Growatt's poll interval unless the simulator runs. |
| Energy page | Water-heater energy counter card and battery card picked by device kind. | Installation solar and household power, plus battery power of the device that reports `BatteryPower`. No invented totals; longer ranges are on History. |
| Dashboard catalog | Listed `simulator:` sources with "simulated" provenance. | Listed only while enabled. Saved widgets that use them show "The source is no longer available for this property." |
| 7A history listing, raw, hourly and daily reads, and 7B logical history | Included `simulator` series. | Excluded unless enabled, filtered centrally in `TelemetryHistoryReader`. A logical segment on simulated hardware reads as unsupported. |
| 7B initialization and 7E provisioning | Would create identities for simulator devices on the real property. | Simulated devices are skipped ("simulated device"). They are also excluded from candidates, the physical-device list and new assignments. Existing assignments still display. |
| Topbar | "Simulated data" on Devices and Energy. | "Property monitoring". |

Retained for tests and development: `SimulatorProvider`, `SimulatorOptions`, `TelemetryWorker`, `RegisterDemoInventoryAsync`, `PersistAsync`, the seeded `solar` installation and `simulator` connection (migration 002), and the simulator scenario tests. Simulator rollup hold rules stay so stored data aggregates consistently until it is removed.

## Ownership

All decisions use explicit provenance within one property, never device names.

- **Simulator device.** Bound to the property's `simulator` integration connection, bound to no other connection, with an inventory key starting `simulator:`, and with no non-simulator telemetry series.
- **Simulator series.** `telemetry_series.source_key = 'simulator'`, either on an endpoint of a simulator device or on an installation (the seeded `solar` installation).
- **Simulator-only logical device.** Every assignment and its origin refer to simulator devices.
- **Preserved and reported.** These rows are kept, and the dry run lists each one with its reason:
  - a device also bound to a real provider, a simulator binding without a simulator inventory key, or a device with real series;
  - a logical device that also has real hardware;
  - simulated hardware referenced by such a logical device, together with its series;
  - a simulator series on any kept hardware.

## Cleanup mechanism

The command is `--cleanup-simulator-data --simulator-property=<id>` (see the [README](../../README.md#remove-simulator-data)).

- **Dry run (default).** Read-only. It reports:
  - planned row counts per table: samples, latest readings, hourly rollups, rollup state, series, logical audit, assignments and identities, device alerts, state, capabilities, endpoint bindings, bindings, installation memberships, endpoints and devices;
  - which optional tables are absent;
  - preserved records;
  - dashboard values that use a `simulator:` source or a logical device the cleanup removes.
- **Execution.** Requires both `--execute` and `--confirm-simulator-data-deletion`, and is refused while `Simulator:Enabled` is true.
  1. It re-plans and refuses a plan that no longer matches the data.
  2. It deletes raw samples of the planned series in batches of 5,000, each committed on its own.
  3. In one transaction, holding the property row lock and the telemetry rollup lock, it re-plans again. It then deletes hourly rollups, rollup state, latest readings, remaining samples, series, the simulator-only logical identities with their assignments and audit, and the device rows in foreign-key order.
- **Failure and rerun.** A failure before commit rolls the structural transaction back, so nothing is left inconsistent. Already committed sample batches stay deleted, which is harmless because those series are hidden. Rerunning completes the work, and a completed cleanup reports nothing to delete.
- **Never deleted.**
  - Provider telemetry and real devices.
  - Logical identities with real history.
  - Properties, rooms, installations, the simulator connection seed, users, Configuration Vault rows and OAuth grants.
  - Dashboard layouts: widgets, groups, colors and order are never modified. Simulator bindings are reported for an explicit edit in the dashboard editor; there is no automatic removal or remapping.
- **Not reachable from startup or migration.** The command runs only when invoked explicitly.
- **Schema compatibility.** It runs on any schema with the base migrations. Rollup (017), logical (018/019) and dashboard (011) tables are used when present and reported when absent.
- **Audit.** Simulator-only logical identities are deleted together with their audit rows, since both describe only demonstration hardware. The command output (planned counts, preserved records and rows deleted per table) is the cleanup record; keep it with the backup.

### Permissions

Planning needs `SELECT`, which the runtime account has. Execution needs `SELECT, DELETE` on the schema and `UPDATE` on `properties`, for the row lock. Use a dedicated maintenance account or the migration account. The runtime account's DML-only grants are unchanged. A missing privilege stops the command before anything is deleted.

## Deployment procedure (later, with explicit approval)

1. **Check the deployment.** Note the running commit and run `SELECT name FROM schema_migrations ORDER BY name` with a read-only account. Confirm no `Simulator__Enabled` environment variable or User Secret is set to `true`.
2. **Back up.** Take a full `mysqldump --single-transaction --routines` backup and verify it, for example by restoring it into a scratch schema and counting `telemetry_samples` per `source_key`.
3. **Migrate.** Apply pending migrations with the migration runner, which adds nothing for this change.
4. **Restart.** Restart the backend with this version; the simulator stays disabled.
5. **Confirm no new simulator data.** After a few minutes, `SELECT MAX(h.observed_at_utc) FROM telemetry_samples h JOIN telemetry_series s ON s.id=h.series_id WHERE s.source_key='simulator'` must not advance. The API logs no "Simulator observations persisted".
6. **Dry run.** Run the cleanup dry run for each property listed by `SELECT DISTINCT property_id FROM integration_connections WHERE provider_code='simulator'`.
7. **Review.** Check the counts, every preserved record and every dashboard reference. Investigate any unexpected preserved record before continuing.
8. **Execute.** Only after explicit approval, run the command with `--execute --confirm-simulator-data-deletion` and the maintenance account. Save the output.
9. **Verify provider data.** Before and after, compare `COUNT(*)` and `MAX(observed_at_utc)` of `telemetry_samples` grouped by `source_key` for every non-simulator source. They must be identical apart from new live data.
10. **Verify the application.** Check Dashboard (simulator widgets show unavailable until edited), Devices (real hardware only), Energy, and History (no simulator series).
11. **Confirm idempotence.** Rerun the dry run; it must report nothing to delete.

### Rollback and recovery

- **Reversible by settings.** Hiding: setting `Simulator:Enabled=true` in an isolated environment shows stored simulator data again, and setting it back hides it. Restore the previous application version to undo the code change.
- **Not reversible by settings.** Deleted rows return only by restoring the backup taken in step 2, either completely or by restoring the backup into a scratch schema and copying the affected property's rows back. Re-enabling the simulator never restores deleted history: it only writes new demonstration data.
- **Dashboard edits.** Dashboard edits made after cleanup are ordinary layout changes and are not undone by any restore except a full backup restore.

## Verified and unverified

- **Verified on disposable MySQL 8.0.46:**
  - hidden simulator data in state, history, catalog and logical management;
  - a read-only dry run with exact counts, preserved mixed records and dashboard references;
  - execution that leaves real samples, rollups, devices, mixed logical history, dashboards and seeded rows unchanged;
  - idempotent reruns, rollback of a failed structural transaction and recovery on rerun;
  - refusal of a stale plan, and a schema without the optional tables;
  - the CLI dry run with the runtime account, execution with a `SELECT, DELETE` plus `UPDATE(properties)` account, and refusal with the runtime account.
- **Not verified:** the live database contents. The live property may hold months of simulator samples at 10-second resolution, so the batch phase may take minutes. It is resumable.
