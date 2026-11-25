# HomeAutomation — Milestone 7B: Logical Devices, Physical Device Replacement and Continuous History

Date: 2026-11-26

## Original prompt

# HomeAutomation — Milestone 7B: Logical Devices, Physical Device Replacement and Continuous History

Implement Milestone 7B of HomeAutomation.

The objective is to introduce stable logical device identities so physical devices can be replaced without losing their historical data, monitoring identity, or future dashboard continuity.

This is a production-oriented implementation with database migrations, backend APIs, authorization, tests, and a minimal device-replacement UI.

Use extended/high reasoning for architecture, transaction safety and historical data correctness.

Milestone 7C (Historical Charts) is explicitly out of scope.

## 0. Project context

Before implementation:

1. Read `CLAUDE.md`, `AGENTS.md` and `docs/AI_HANDOFF.md`.
2. Review relevant architecture documents and ADRs.
3. Read `docs/architecture/10-historical-telemetry.md`.
4. Inspect the existing devices, endpoints, capabilities, telemetry series and history readers.
5. Inspect dashboard source binding and persistence.
6. Inspect the existing authorization and audit patterns.
7. Inspect recent commits and current Git status.

Milestone 7A was implemented in commit `754615e`.

It introduced:

- Migration `017_telemetry_rollups.sql`.
- Materialized hourly telemetry summaries.
- Calendar-day history calculated from hourly summaries and partial raw-data intervals.
- Historical APIs with raw/hourly/daily resolution.
- Coverage, quality and energy provenance.
- A bounded aggregation worker.
- An explicit backfill command.

Milestone 7A has not yet been deployed to the live development database.

Do not modify migration 017 or assume it has already been applied to the live database.

Preserve the existing .NET 10, Dapper, MySQL, React/TypeScript, SignalR and Configuration Vault architecture.

Produce a concise implementation plan before editing code.

## 1. Main objective

Separate the permanent identity of a monitored location or equipment role from its replaceable physical hardware.

Examples:

- Living room temperature sensor.
- Outdoor temperature sensor.
- Heat pump power meter.
- Boiler power relay.
- Bedroom temperature and humidity sensor.

A logical device must preserve its identity even when its physical device changes.

Example:

Logical device: "Living Room Temperature"

Physical assignments:

- Tuya Sensor A — from 2025-01-01 until 2026-11-26 14:37.
- Tuya Sensor B — from 2026-11-26 14:37 onward.

Historical queries must return one continuous logical temperature series containing measurements from both sensors.

The original physical observations must remain unchanged and independently traceable.

A replacement may happen across providers.

Examples:

- Tuya sensor → another Tuya sensor.
- Tuya temperature sensor → compatible eWeLink sensor.
- Tongou electrical relay → Sonoff electrical relay.

No historical measurements may be deleted or re-keyed.

## 2. Data model

Introduce a minimal logical-device model using the existing MySQL migration framework.

Inspect the existing schema before finalizing table definitions.

Expected new migration:

`018_logical_devices.sql`

Suggested conceptual tables:

### logical_devices

Fields should include:

- Stable logical device ID.
- Property ID.
- Name.
- Optional room or location.
- Logical device kind or role.
- Optional description.
- Created/updated timestamps.
- Archived status.

Logical devices belong to exactly one property.

Use existing room references where available.

### logical_device_assignments

Each assignment associates a logical device, optionally a measurement channel, with an existing physical device or endpoint.

Include:

- Assignment ID.
- Property ID.
- Logical device ID.
- Physical device/endpoint ID.
- Channel identity when needed.
- Effective start UTC.
- Effective end UTC, nullable for active assignments.
- Assigned by.
- Assignment timestamp.
- Optional replacement reason.

Use half-open validity intervals:

`[valid_from_utc, valid_to_utc)`

A reading exactly at the replacement timestamp belongs to the new assignment, not both.

Preserve all previous assignments.

Consider whether assignment identity should operate at the device level, endpoint level, or logical measurement-channel level.

Choose the simplest structure compatible with the existing capability model, and document the decision.

Do not assume every physical device has only one measurement capability.

## 3. Assignment constraints and integrity

Enforce the following invariants:

- No overlapping assignments for an exclusive logical measurement channel.
- No two simultaneously active physical sources for the same exclusive logical channel.
- A physical endpoint cannot be assigned incompatibly to conflicting logical roles.
- No cross-property assignments.
- Assignment intervals must be valid.
- A replacement cannot silently overwrite previous assignments.
- Archived logical devices retain their history.
- Historical corrections must be explicit and audited.

Use appropriate MySQL constraints, indexes and transactions.

Because MySQL does not provide general exclusion constraints, use transactional locking and application-level interval validation for overlapping historical intervals.

Handle concurrent replacement requests safely.

Do not rely only on frontend validation.

## 4. Initial logical-device bootstrap

We want existing devices to remain usable immediately after the new feature is deployed.

Implement a safe, deterministic and idempotent initialization process that creates logical identities for existing monitored physical devices.

Requirements:

- Preserve the existing physical device inventory.
- Preserve device IDs and provider identities.
- Reuse existing property associations.
- Create appropriate initial logical assignments.
- Associate known historical observations with the correct physical device.
- Do not invent historical readings.
- Do not guess past replacements.
- Do not create duplicate logical devices if initialization runs more than once.
- Avoid creating duplicate logical identities for different source streams representing the same physical sensor.

Prefer a controlled, explicitly documented initialization mechanism when schema migration alone cannot determine correct assignments.

Do not execute initial assignment backfill against the live database automatically.

Document how the initialization works for existing data and devices with no historical samples.

## 5. Physical device replacement

Implement an atomic replacement operation.

For an existing logical device and active physical assignment:

1. Validate the user's permissions.
2. Validate property ownership.
3. Validate the replacement device exists.
4. Validate measurement capability compatibility.
5. Validate that the replacement device is not already assigned incompatibly.
6. Validate the effective replacement timestamp.
7. Close the previous assignment.
8. Create the new assignment.
9. Record the change in the audit trail.
10. Commit the operation atomically.

If any validation or database operation fails, roll back the entire replacement.

Use server-side UTC timestamps.

Allow an explicitly selected historical replacement timestamp under the required permissions.

### Important: arbitrary replacement time

Do not round device replacements to the beginning of an hour.

A replacement at 14:37 must be supported accurately.

Historical data before 14:37 belongs to the old assignment.

Historical data at or after 14:37 belongs to the new assignment.

Do not silently attribute the entire 14:00–15:00 hour to one device.

## 6. Capability compatibility

The replacement process must inspect physical-device capabilities.

Examples:

### Compatible replacement

Old device:
- Temperature.

New device:
- Temperature.
- Humidity.

Result:
- Temperature history continues.
- Humidity starts when reliable humidity data becomes available.
- Historical humidity is not fabricated.

### Partial replacement

Old device:
- Temperature.
- Humidity.

New device:
- Temperature only.

Result:
- Temperature continues.
- Historical humidity remains accessible.
- New humidity observations are absent.
- The UI explains the missing capability.

### Electrical replacement

Old device:
- Power W.

New device:
- Power W.
- Energy kWh.

Result:
- Power history continues across the replacement.
- Energy history follows the available measurement capabilities.
- Physical energy counters remain distinct.
- No counter delta is calculated between different devices.

Incompatible or ambiguous replacements must require explicit resolution or be rejected.

Do not match unrelated metrics merely because they have similar labels.

Use canonical metric identities and units.

## 7. Continuous logical history

Extend the Milestone 7A historical query infrastructure so it can return measurements for a logical device across its assignment history.

Support:

- Logical device ID.
- Metric.
- Time range.
- Resolution: raw, hourly or daily.

For each historical interval:

1. Identify the active physical assignment.
2. Resolve compatible telemetry series.
3. Restrict observations to the assignment's effective interval.
4. Return the appropriate historical measurements.
5. Preserve source and quality metadata.

Historical observations must remain physically owned by their original telemetry series.

Do not rewrite `telemetry_samples`.

Do not rewrite existing 7A aggregates.

### Hourly aggregation across replacements

The existing 7A hourly rollups are keyed by physical telemetry series.

That design must remain unchanged.

For a logical device replacement occurring inside an hour:

- Use the original hourly rollups for fully covered assignment intervals.
- Use raw telemetry where exact partial-hour calculation is required.
- Avoid double-counting.
- Preserve coverage information.
- Do not add time-weighted averages without considering interval coverage.
- Respect 7A freshness and gap policies.

For example, a replacement at 14:37 must produce a correct logical result for the 14:00–15:00 hour.

### Daily aggregation

Combine logical-device history across physical assignments using the property time zone.

Handle:

- DST transitions.
- Missing data.
- Partial hours.
- Multiple physical assignments within one day.
- Estimated versus measured energy.
- Different providers and sample frequencies.

Daily averages must be correctly weighted.

Do not calculate daily averages by simply averaging hourly averages.

Do not treat periods without aggregated data as zero.

### Energy counters

Never calculate an energy delta between the last reading of the old physical device and the first reading of the new device.

Preserve per-device counter boundaries, resets and provenance.

Measured energy and estimated energy must remain separately identifiable.

## 8. Source selection

The existing telemetry model may contain multiple source streams for the same physical metric.

For example, a Tuya thermometer may have:

- `tuya`
- `tuya-sensor-report`

Milestone 7A prefers `tuya-sensor-report` and exposes other streams as alternatives.

Preserve that rule unless repository inspection identifies a better existing implementation.

Requirements:

- Avoid duplicate readings.
- Avoid silently combining different physical or provider streams.
- Provide deterministic source selection.
- Preserve provenance.
- Allow future source-selection changes without rewriting raw history.

## 9. API design

Implement authenticated, property-authorized APIs for:

- Listing logical devices.
- Reading one logical device.
- Creating a logical device.
- Updating its name, room and descriptive metadata.
- Listing compatible physical devices.
- Assigning a physical device.
- Replacing a device.
- Viewing assignment history.
- Archiving a logical device.
- Querying historical telemetry by logical device.

Reuse existing API conventions.

Avoid unnecessary new endpoint patterns.

Owner and Admin may perform replacement and assignment changes.

Preserve the existing read authorization model for history.

All mutations must enforce property membership and role checks on the server.

Return appropriate validation errors and conflict responses.

Provide bounded queries and pagination where appropriate.

## 10. Minimal replacement UI

Implement a functional UI using the existing Devices section.

Do not redesign the entire application.

Required functionality:

- Display logical devices and their names.
- Show the currently assigned physical device.
- Show provider and relevant capabilities.
- Display previous physical assignments.
- Provide a "Replace device" action.
- Allow selection of an available compatible replacement.
- Show capability differences and warnings.
- Allow the replacement effective time to be selected.
- Require explicit confirmation before saving.
- Display success or meaningful validation errors.

Use existing UI components and styling.

Keep the replacement operation understandable for nontechnical users.

Example:

Logical device: Heat Pump Power Meter

Current device: Tongou PSF-X67

Replacement: Sonoff power-monitoring device

Effective time: 26 Nov 2026, 14:37

Before confirmation, explain that historical readings will be preserved and future readings will use the replacement device.

Do not pair, reset or configure physical devices as part of this operation.

Only select from devices already available in the system.

## 11. Dashboard continuity

Review existing dashboard source identifiers.

Current dashboard sources are associated with provider-specific physical devices.

Introduce a stable logical source identifier, conceptually:

`logical:{logicalDeviceId}:{channel}:{metric}`

Adapt the exact structure to existing conventions.

Requirements:

- Existing dashboard widgets must continue working.
- Do not break saved dashboard layouts.
- Allow widgets to bind to a logical device.
- A widget bound to a logical device must resolve its current active physical source.
- When the physical device is replaced, that logical widget should follow the new device without requiring manual recreation.
- Unsupported capabilities must display an appropriate unavailable state.
- Never show a stale reading from the retired device as if it came from the replacement.

Provide a safe, explicit way to convert existing physical-device widget bindings to logical bindings.

Do not silently rewrite ambiguous dashboard configurations.

This milestone needs only the source-binding functionality.

Do not implement new historical charts, chart styling, or advanced analytics.

## 12. Provider compatibility

Preserve all currently working providers:

- Growatt.
- Daikin ONECTA.
- eWeLink / Sonoff / Tongou.
- Tuya / Smart Life.
- Simulator.

A logical replacement is a database-level mapping operation.

It must not directly change:

- Provider credentials.
- OAuth grants.
- Configuration Vault profiles.
- Polling intervals.
- Cloud device discovery.
- Physical relay states.
- Inverter configuration.
- Device-control permissions.

### Growatt-specific warning

The existing Growatt binding logic validates configured physical identities.

A future inverter replacement may require coordination between physical binding configuration and logical assignment history.

Inspect the current behavior carefully.

Do not weaken or bypass the existing identity guard merely to support logical replacement.

If a secure Growatt hardware replacement requires a separate configuration workflow, document it explicitly instead of introducing an unsafe workaround.

## 13. Auditing and traceability

Record logical-device assignment changes using existing audit conventions.

Include:

- Property ID.
- Logical device.
- Old physical device.
- New physical device.
- Effective time.
- Acting user.
- Operation time.
- Optional reason.

Provide enough information to understand historical measurement discontinuities later.

Do not expose secrets, provider tokens or private configuration data.

## 14. Testing

Add thorough backend and frontend tests.

### Schema and initialization
- Migration from a populated 017 database.
- Existing data remains unchanged.
- Logical-device bootstrap is idempotent.
- Devices with and without historical telemetry.
- Multiple endpoints and capabilities.

### Replacement
- Successful replacement.
- Cross-provider replacement.
- Replacement at an arbitrary timestamp.
- Same-hour replacement.
- Backdated replacement.
- Repeated replacement.
- Incompatible capabilities.
- Conflicting active assignments.
- Overlapping historical intervals.
- Concurrent replacement requests.
- Transaction rollback.
- Property isolation.
- Authorization failures.

### Historical continuity
- Temperature across two sensors.
- Humidity history when the new device lacks humidity.
- Power across two relays.
- Hourly aggregation across the replacement timestamp.
- Daily aggregation spanning replacement.
- DST boundary.
- Missing observations.
- Provider source preference.
- No double-counting.
- No energy counter deltas across devices.
- Measured versus estimated energy separation.
- Unaggregated historical periods remain distinguishable from zero.

### Dashboard
- Existing physical-source widgets remain valid.
- Logical-source widgets resolve the active physical device.
- Widgets follow a replacement.
- Retired-device readings are not presented as current.
- Missing capabilities display unavailable status.
- Existing saved layouts are not damaged.

### Security
- Owner/Admin replacement permissions.
- Unauthorized property access.
- Cross-property assignment rejection.
- Audit-trail integrity.

Use synthetic data, mocked providers and disposable MySQL integration tests.

Do not contact physical devices.

## 15. Database and deployment safety

Create the next versioned migration after 017.

Do not modify migrations 014–017.

Use the existing migration framework and checksum ledger.

Do not apply migrations to the live database.

Do not run initialization or backfill against the live database.

Do not restart the running backend.

Do not change Configuration Vault settings.

Do not execute any provider-control operation.

Do not push to GitHub.

### Deployment documentation

Provide the correct future deployment sequence:

1. Create a database backup.
2. Apply all pending migrations, including 017 and 018, through the existing migration runner.
3. Restart the backend with the compatible application version.
4. Run the approved 7A backfill after a dry-run review.
5. Run the approved logical-device initialization if required.
6. Validate logical identities and existing dashboard functionality.
7. Verify historical reads and a safe replacement scenario.

These steps are documentation only. Do not execute them against the live environment.

## 16. Scope boundaries

Do not implement Milestone 7C.

Specifically exclude:

- New historical charts.
- Advanced graph interactions.
- Historical chart comparisons.
- Chart exports.
- Monthly and yearly analytics dashboards.
- New provider integrations.
- Provider polling improvements.
- New cloud API calls.
- Device-control automation.
- Retention enforcement.
- Unrelated refactoring.

The replacement management UI and logical dashboard source bindings are part of 7B and must be included.

## 17. Implementation sequence

Implement in a logical order:

1. Validate the existing device, telemetry and dashboard architecture.
2. Design the logical-device schema.
3. Implement migration 018.
4. Implement idempotent initialization for existing devices.
5. Implement transactional assignment and replacement services.
6. Implement historical queries across assignments.
7. Implement property authorization and auditing.
8. Implement logical dashboard source resolution.
9. Implement the minimal replacement management UI.
10. Add backend and frontend tests.
11. Update architecture documentation and handoff.

If necessary, split the work into small internal checkpoints.

Do not sacrifice correctness to finish every item in one pass.

## 18. Completion requirements

At completion:

1. Summarize the implemented functionality.
2. Explain the final logical-device architecture.
3. Explain how physical devices and measurement channels are mapped.
4. Document replacement and historical aggregation behavior.
5. List new database migrations.
6. Explain initialization of existing logical devices.
7. Provide exact deployment and validation commands.
8. Report backend build and test results.
9. Report frontend build and test results.
10. Document unresolved edge cases and deferred functionality.
11. Update relevant architecture documentation and ADRs.
12. Update `docs/AI_HANDOFF.md`.
13. Archive this prompt in `docs/prompts`.
14. Commit locally with a descriptive commit message.
15. Do not push.

Do not proceed to Milestone 7C.

The completed implementation must preserve historical measurements and existing device functionality while allowing physical hardware to be replaced safely and transparently.

## Execution summary

- Validated the architecture at `754615e`. Physical identity is per provider record. Tuya splits metrics across data-point endpoints, while eWeLink and Daikin use one endpoint. Dashboard keys are provider-specific. The Growatt guard rejects bindings that differ from its configured serials.
- Added `database/migrations/018_logical_devices.sql`, with logical devices, channel assignments over half-open intervals, and an append-only audit. Added `HomeAutomation.Domain/LogicalDevices.cs` (overlap, capability comparison and the segment planner) and `HomeAutomation.Application/LogicalDevices.cs`.
- Added `LogicalDeviceStore`, with transactional create, update, assign, replace, correct and archive under a property row lock, plus candidates and idempotent initialization. Extended `TelemetryHistoryReader` with assignment segments that use only in-interval observations; physical 7A history is one open segment. Extended `DashboardSources` and `MonitoringDashboardStore` with logical sources and the explicit conversion review and apply.
- Added the `logical-devices` routes, `dashboard/logical-bindings` and `--initialize-logical-devices`. Scoped the 7A rollup lock to the current schema.
- Added the Logical devices section and Replace device flow to the Devices page, and logical source states in dashboard widgets and the editor.
- Documented the design in `docs/architecture/11-logical-devices.md`, an ADR 0007 addendum, the data model, the 7A document, the README deployment sequence and the handoff.
- Verification: the Release build passed with zero warnings. Backend tests reported 265 passed and 11 skipped, run twice, with disposable MySQL 8.0.46 on loopback port 3417 and DML-only runtime accounts. The frontend build and 57 Vitest tests passed. The CLI initialization dry run wrote nothing. Nothing ran against the live database, the backend was not restarted, and no provider or device was contacted. Graphify is not installed, so `graphify update .` was not run.
