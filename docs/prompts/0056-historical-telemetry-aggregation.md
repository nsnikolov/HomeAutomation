# HomeAutomation — Milestone 7A: Historical Telemetry, Aggregation and History APIs

Date: 2026-11-25

## Original prompt

# HomeAutomation — Milestone 7A: Historical Telemetry, Aggregation and History APIs

Implement Milestone 7A of HomeAutomation: provider-neutral historical telemetry, hourly aggregation, calendar-day reporting, energy estimation and historical query APIs.

This is a production-oriented implementation, not a prototype.

Use your completed Milestone 7A + 7B architectural review as a starting point, but validate all findings against the current code before implementation.

Use extended reasoning for architectural and data-integrity decisions.

## 0. Read project context

Before modifying code:

1. Read `CLAUDE.md`, `AGENTS.md` and `docs/AI_HANDOFF.md`.
2. Review relevant architecture documents, ADRs and recent commits.
3. Inspect Git status.
4. Review the existing telemetry infrastructure and database schema.
5. Identify existing telemetry ingestion, persistence and history query paths.
6. Verify current measurement units, device identities and property authorization rules.

Preserve the established .NET 10, Dapper, MySQL, React, TypeScript and SignalR architecture.

Do not introduce Entity Framework, InfluxDB, additional message brokers or unnecessary infrastructure.

Produce a concise implementation plan before editing.

## 1. Architecture findings to reuse

Your previous read-only investigation identified:

- `TelemetryWriter.PersistAsync` as the shared persistence path.
- `TelemetryWriter.StoreAsync` for Growatt.
- `TelemetryWriter.StoreCloudDeviceAsync` for cloud devices.
- `telemetry_series` for provider-neutral series identity.
- `telemetry_samples` for timestamped raw observations.
- `latest_readings` for current values.
- A provider-neutral `Metric` enum with canonical units.
- `properties.time_zone` for property calendar semantics.
- Existing history readers with different provider-specific limitations.

Important existing behavior:

- Raw samples use `(series_id, observed_at_utc)` as the primary key.
- Duplicate timestamps currently follow first-write-wins behavior.
- Some devices have more than one source stream.
- Growatt reports daily-reset energy counters.
- Tuya and eWeLink currently provide electrical power readings but not reliable cumulative kWh counters.
- Existing history API functionality is incomplete for real devices.

Validate these assumptions and reuse the current structures.

Do not create a second raw telemetry storage system.

## 2. Required measurement coverage

Implement historical aggregation and query support for the following capabilities wherever the current provider supplies reliable data:

| Measurement | Unit | Hourly statistics |
|---|---|---|
| Temperature | °C | Average, minimum, maximum |
| Humidity | % | Average, minimum, maximum |
| Power | W | Average, minimum, maximum |
| Voltage | V | Average, minimum, maximum |
| Current | A | Average, minimum, maximum |
| Energy | Wh / kWh | Interval energy where derivable |
| Relay state | On / Off | Supported ON duration and coverage |

Also support existing compatible provider-neutral metrics where appropriate.

Implement this through metric capabilities rather than individual device-model hardcoding.

The goal includes historical data from:

- Tuya thermometers.
- eWeLink temperature sensors.
- Tuya power-monitoring devices.
- eWeLink/Tongou power-monitoring relays.
- Growatt telemetry where applicable.
- Daikin temperature and power measurements where available.
- Simulator measurements.

Do not fabricate unsupported measurements.

Do not add provider API requests or change existing polling intervals.

## 3. Raw telemetry integrity

Preserve the original physical source and observation timestamps.

Requirements:

- Store timestamps in UTC.
- Preserve provider observation timestamps when available.
- Keep ingestion time distinguishable from observation time.
- Preserve existing timestamp provenance.
- Handle duplicate observations idempotently.
- Handle late and out-of-order observations.
- Reject invalid numeric values such as NaN or infinity.
- Ignore missing readings rather than converting them to zero.
- Never turn stale last-known values into new observations.
- Preserve canonical units and metric identity.
- Ensure all data remains associated with the originating physical device and telemetry series.

Review the existing first-write-wins semantics.

Do not silently change them to last-write-wins.

If correction handling is needed, define an explicit and testable correction mechanism.

## 4. Hourly aggregation

Introduce materialized hourly aggregates in MySQL.

Create the next available versioned migration, expected to be `017_telemetry_rollups.sql`, after inspecting the repository.

Prefer aggregates keyed by:

- Existing telemetry series ID.
- UTC bucket start.

For appropriate metrics, store:

- Sample count.
- Minimum value.
- Maximum value.
- Arithmetic or time-weighted average as appropriate.
- First and last observation timestamps.
- Last observed value.
- Covered seconds.
- Aggregation calculation timestamp.
- Energy interval delta, where supported.
- Estimated positive and negative energy, where applicable.
- Counter reset and data-quality indicators.

Do not assume every metric requires every aggregate column.

Use appropriate indexes for time-range queries.

Avoid unnecessary storage duplication.

### Time-weighted averages

For irregularly sampled measurements, use bounded interval weighting where defensible.

Never extrapolate an observation indefinitely.

A gap beyond the configured maximum hold interval must remain uncovered.

Keep raw observation count separate from covered seconds.

Document whether each reported average is arithmetic or time-weighted.

## 5. Daily aggregation and time zones

Do not initially create a separate persisted daily table.

Calculate daily summaries using hourly data where this produces correct results.

Requirements:

- Store hourly buckets in UTC.
- Use `properties.time_zone` for local calendar reporting.
- Handle 23-hour and 25-hour DST transition days.
- Support IANA time zones with non-whole-hour UTC offsets.
- Never assume that local midnight aligns with a UTC hour boundary.
- Do not misattribute measurements across local-day boundaries.

If hourly rollups cannot accurately represent a partial-hour local-day boundary, use an accurate bounded raw-data calculation or an explicit partial-bucket mechanism.

Do not silently return incorrect daily aggregates.

Ensure daily averages are weighted by valid coverage rather than simply averaging hourly averages.

Daily minimum and maximum must reflect all valid contributing observations.

## 6. Electrical energy calculations

This section requires particular care.

### 6.1 Power-derived energy

For devices reporting instantaneous power in watts:

- Estimate energy by integrating power over supported time intervals.
- Use a documented bounded integration strategy.
- Apply a maximum supported interpolation or hold gap.
- Never assume missing telemetry means zero consumption.
- Never project the last observed wattage across an arbitrarily long offline period.
- Track coverage and confidence.
- Report estimated energy separately from measured energy.

Use Wh internally where practical and convert to kWh for presentation.

### 6.2 Device energy counters

When a provider supplies reliable cumulative or daily-reset energy counters:

- Calculate interval deltas correctly.
- Detect counter resets.
- Handle discontinuities and possible rollover.
- Do not subtract readings across different physical devices.
- Do not combine counter values from unrelated streams.
- Avoid assigning an entire unobserved interval's energy to an arbitrary hour without an explicit quality indication.

If exact per-hour attribution cannot be established, expose the limitation instead of claiming precise hourly energy.

### 6.3 Growatt-specific semantics

Preserve existing Growatt aggregation rules.

Handle:

- Daily-reset PV energy.
- Daily output energy.
- Battery discharge energy.
- Master and Slave inverter roles.
- Shared battery measurements.
- Signed power measurements.
- Possible double-counting between device and installation-level metrics.

The installation total must not blindly sum physical series that represent shared equipment.

Use the established provider mappings and time-zone semantics.

### 6.4 Estimated versus measured

Expose explicit provenance such as:

- Measured.
- Estimated from power.
- Incomplete.
- Unsupported.

Never silently combine estimated and measured values into a single authoritative energy total.

## 7. Relay ON/OFF history

Where relay state observations are available:

- Preserve transitions.
- Calculate supported ON duration.
- Account for gaps and offline periods.
- Do not assume a relay remains ON indefinitely when communication is lost.
- Do not infer physical switching from stale cloud status.
- Report covered seconds and ON seconds separately.
- Reject invalid or contradictory state readings safely.

Do not issue relay control commands.

## 8. Data freshness, missing data and coverage

Implement explicit data coverage reporting.

Approved default policy:

- Use provider-appropriate maximum observation gaps.
- Respect the current polling interval and reporting behavior.
- Calculate coverage as supported seconds divided by the period duration.
- Flag coverage below 80% as incomplete.
- Keep unknown intervals unknown.
- Never create artificial zero readings.

Coverage calculations must work across hourly and daily boundaries.

Expose coverage and data-quality information through history APIs so future charts can display gaps accurately.

Do not confuse an online device with complete historical coverage.

## 9. Aggregation worker

Implement a bounded background aggregation worker using existing hosting conventions.

Requirements:

- Respect migration readiness and schema version checks.
- Recompute the most recent 72 hours to account for late observations.
- Use idempotent writes or upserts.
- Avoid duplicate counts.
- Avoid overlapping concurrent aggregation work.
- Use bounded batches and appropriate cancellation tokens.
- Keep database load predictable.
- Do not block ordinary telemetry ingestion.
- Avoid excessive CPU or memory consumption.
- Recover safely after restarts.

The worker must not delete raw history.

Review the existing MySQL transaction and locking patterns.

Use the simplest approach that satisfies correctness.

## 10. Historical backfill

Provide an explicit command to aggregate existing raw observations.

Requirements:

- Dry-run support.
- Explicit execution mode.
- Bounded property, series and time-range selection.
- Report the affected range and expected workload.
- Process in manageable batches.
- Be safe to rerun.
- Preserve raw telemetry.
- Avoid duplicate aggregates.
- Handle cancellation and partial completion.
- Support recovery from an interrupted run.
- Do not create missing historical observations.

Backfill must not execute automatically against the live database.

Provide exact deployment commands in the documentation.

If older data has not been collected, clearly explain that it cannot be reconstructed.

## 11. Provider-neutral history APIs

Extend the existing history infrastructure to support all relevant providers.

Expose property-authorized read APIs supporting:

- Raw history.
- Hourly history.
- Daily history.

Support filters for:

- Property.
- Device or telemetry series.
- Measurement type.
- Time range.
- Resolution.

Requirements:

- Enforce property authorization.
- Validate requested time ranges.
- Apply pagination or maximum result limits.
- Return timestamps and units consistently.
- Include data coverage and quality metadata.
- Return gaps correctly.
- Avoid reading telemetry from unrelated properties.
- Avoid unnecessary duplicate data when multiple source streams represent the same metric.

Define explicit source-selection behavior where required.

Existing provider-specific endpoints must remain operational.

Do not break the current dashboard, monitoring pages or device history readers.

Design APIs so Milestone 7B can later compose a logical device's history across multiple physical devices and arbitrary replacement timestamps.

## 12. Future compatibility with logical devices

Do not implement logical devices in this milestone.

However, support their future requirements:

- Historical raw samples remain owned by physical series.
- Hourly aggregates remain associated with their original physical series.
- Logical-device history will be assembled using timestamped assignments.
- Replacement timestamps may occur at any instant, not just at an hourly boundary.
- Partial-hour attribution must be accurate.
- Counter deltas must never cross a physical device replacement.
- Historical data must remain accessible after a device is retired.

Avoid architectural choices that would require rewriting existing telemetry after replacement.

Milestone 7B will implement logical devices and replacement workflows.

## 13. Storage and retention

Use MySQL 8.0 and Dapper.

Do not introduce a new database technology.

Approved retention decision:

- No automatic deletion of raw history.
- No automatic aggregate deletion.
- No retention enforcement in 7A.

Document estimated storage growth for typical sensor counts and polling frequencies.

Add appropriate indexes.

Avoid unnecessary duplication or full-table scans during normal operation.

Retention policies can be implemented in a future milestone after measuring actual storage growth.

## 14. Database migrations

Inspect the latest migration before assigning the new version.

Migrations 014, 015 and 016 are already deployed and must remain unchanged.

Requirements:

- Use the existing migration runner.
- Preserve checksums and migration history.
- Do not modify previously applied migrations.
- Avoid broadening runtime database permissions unnecessarily.
- Verify upgrades from an existing populated schema.
- Document required database grants, if any.
- Do not apply migrations to the live development database.

Use disposable MySQL instances for database integration testing.

## 15. Tests

Add comprehensive tests covering at least:

### Telemetry integrity
- Duplicate samples.
- First-write-wins behavior.
- Out-of-order samples.
- Late observations.
- Missing and invalid values.
- Timestamp provenance.
- Duplicate provider source streams.

### Aggregation
- Numeric minimum and maximum.
- Arithmetic averages.
- Time-weighted averages.
- Hourly boundary handling.
- Daily aggregation.
- DST transitions.
- Half-hour and quarter-hour UTC offset zones.
- Zero-coverage and partial-coverage buckets.
- Idempotent recomputation.

### Energy
- Constant power integration.
- Variable power integration.
- Long telemetry gaps.
- Positive and negative power.
- Daily-reset counters.
- Counter discontinuities and rollover.
- Missing counter samples.
- Growatt master/slave double-counting prevention.
- Estimated versus measured energy separation.

### Relay state
- ON/OFF transitions.
- ON duration.
- Offline intervals.
- Missing transition observations.

### APIs and authorization
- Property isolation.
- Authorized and unauthorized history access.
- Pagination and bounded queries.
- Correct units and timestamps.
- Data-quality metadata.
- No cross-provider duplicate counting.

### Backfill and persistence
- Repeated backfill.
- Interrupted backfill.
- Late-data recomputation.
- Database transaction failures.
- Migration upgrade from populated schema.
- Background-worker cancellation.

Run all relevant existing backend builds and tests.

Add opt-in disposable-MySQL integration tests where needed.

Automated tests must use synthetic data and mocked providers.

## 16. Scope and safety

This milestone implements backend historical aggregation and APIs.

Do not implement:

- Milestone 7B logical-device replacement.
- Milestone 7C historical charts.
- Dashboard redesign.
- Provider authentication changes.
- Configuration Vault changes.
- New polling APIs or cloud requests.
- Device-control automation.
- Automatic raw-data deletion.
- Unrelated architectural refactoring.

Do not restart the running HomeAutomation application.

Do not apply migrations or backfill against the live database.

Do not contact real provider APIs or control physical equipment.

## 17. Implementation sequence

Proceed in the following order:

1. Validate the existing architecture and data model.
2. Implement and test the pure aggregation calculations.
3. Add the migration and persistence layer.
4. Implement bounded, idempotent hourly aggregation.
5. Implement correct calendar-day querying.
6. Implement energy and relay calculations.
7. Extend provider-neutral historical APIs.
8. Implement explicit dry-run and execution backfill commands.
9. Add regression and integration tests.
10. Update documentation and handoff.

If implementation becomes too large, stop at a stable, tested boundary.

Do not skip correctness or regression tests merely to complete the milestone.

## 18. Completion

At completion:

1. Summarize the implementation.
2. Explain the aggregation model.
3. Document measured and estimated energy behavior.
4. Explain incomplete coverage and gap handling.
5. List new migration files.
6. List any required database permission changes.
7. Provide exact migration and backfill commands.
8. Report backend builds and test results.
9. Document any limitations or deferred decisions.
10. Update relevant architecture documentation.
11. Update `docs/AI_HANDOFF.md`.
12. Archive this prompt under `docs/prompts`.
13. Commit locally without pushing.

Do not execute live deployment operations.

Do not proceed into Milestones 7B or 7C.

The result must be a maintainable and trustworthy historical telemetry foundation for HomeAutomation.

## Execution summary

- Validated the 7A/7B review against `e8d66b2`. Migration 016 was the latest. `Migrations.VerifyAsync` and readiness require every unexcluded script, so 017 is verified separately. eWeLink persists measurements only from WebSocket frames, and electrical frames only during live viewing. Growatt already writes installation-level PV and master site load. The runtime account's schema-wide `SELECT, INSERT, UPDATE` is sufficient.
- Added the pure calculator in `HomeAutomation.Domain/TelemetryAggregation.cs`. It provides capability-based metric kinds, a bounded zero-order hold, time-weighted and arithmetic averages, relay ON time, estimated ±Wh, measured counter Wh with resets and discontinuities, local calendar days, and the hourly/raw planner.
- Added `database/migrations/017_telemetry_rollups.sql`, with `telemetry_rollups_hourly` and `telemetry_rollup_state`. Added the rollup engine, worker, hold policy, history reader and DI registration under `HomeAutomation.Infrastructure/MySql/`. Added the `history` and `history/series` routes and the `--backfill-telemetry-rollups` command under `HomeAutomation.Api/`.
- Documented the model in `docs/architecture/10-historical-telemetry.md`, the README, the data model, ADR 0006 and the handoff.
- Verification: the Release build passed with zero warnings. Tests reported 236 passed and 12 skipped from other opt-in MySQL fixtures. The history MySQL tests ran on a disposable MySQL 8.0.46 instance on loopback port 3417 with a DML-only runtime account. A CLI dry run against that schema wrote nothing. The live database, running backend and provider APIs were not touched, and no migration or backfill was run against live data. The frontend was unchanged and not rebuilt. Graphify was not on PATH, so `graphify update .` was not run.
