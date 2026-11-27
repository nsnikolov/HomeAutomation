# HomeAutomation — Growatt Availability, PV Generation, Device Search and Solar Installation Aggregation Fixes

Date: 2026-11-27

## Original prompt

# HomeAutomation — Growatt Availability, PV Generation, Device Search and Solar Installation Aggregation Fixes

## Objective

Investigate and fix four issues identified during live testing of HomeAutomation.

The application currently uses .NET 10, React/TypeScript, MySQL, SignalR, and provider integrations with Growatt, Daikin ONECTA, eWeLink and Tuya.

These are targeted reliability, correctness and UX improvements.

Investigate the actual implementation and data flow before modifying code.

Do not introduce a parallel monitoring model or unnecessary architectural changes.

## Issue 1 — Provider polling interval and device online status (P1)

### Current problem

Growatt devices are sometimes displayed as Offline even though fresh telemetry is arriving successfully.

Growatt is typically polled every 5 minutes.

An application-side device availability calculation must respect the configured polling interval rather than applying a fixed short timeout.

### Required behavior

Implement an availability rule based on the configured provider/device polling interval.

A device is considered recently reachable when its latest successful, valid observation falls within:

`Freshness window = Polling interval × 1.5`

Example:

- Configured polling interval: 300 seconds.
- Freshness window: 450 seconds (7 minutes 30 seconds).
- Last successful valid reading: 4 minutes ago → Online.
- Last successful valid reading: 6 minutes ago → Online.
- Last successful valid reading: 8 minutes ago → Stale/Offline depending on confirmed connectivity evidence.

Use the actual configured interval, not a hardcoded 300 seconds.

### Important distinction

Maintain separate concepts for:

1. Provider-reported online/offline status.
2. Successful provider/API communication.
3. Latest valid source telemetry timestamp.
4. Last successful ingestion timestamp.
5. Application-calculated availability.
6. Telemetry freshness.

These are not necessarily equivalent.

When Growatt reports Offline but new telemetry is arriving, the primary application status should reflect the evidence of recent communication or observations.

Preserve the provider-reported status as secondary diagnostic information.

Do not silently discard the original provider status.

### Status semantics

Use clear states consistent with the existing architecture, for example:

- Online — recent valid evidence.
- Stale — no sufficiently recent evidence.
- Offline — confirmed disconnection.
- Unknown — insufficient evidence or ambiguous state.

Do not claim confirmed Offline solely because a freshness threshold expired.

Investigate whether the existing status model supports these distinctions before changing it.

### Edge cases

Handle:

- Polling intervals other than 300 seconds.
- Provider API failures.
- Delayed telemetry.
- Duplicate timestamps.
- Out-of-order telemetry.
- Application restarts.
- No reading ever received.
- Future timestamps or clock skew.
- Multiple Growatt inverters.
- Successful API calls that return no new measurements.
- Explicit hardware offline indications.

If a provider request succeeds but returns old data, do not treat the old measurement as freshly observed merely because it was fetched again.

Avoid using `DateTime.Now` inconsistently; use UTC and existing time abstractions.

### Integration with alert rules

The new Notification Rule Engine supports connectivity and stale telemetry conditions.

Ensure the corrected availability model is also used consistently by:

- Device cards.
- Growatt monitoring.
- Dashboard.
- Device status.
- Connectivity signals.
- Notification Rule Engine.

Do not resolve or create incidents incorrectly because of stale status calculations.

Preserve the distinction between connectivity and telemetry freshness.

---

## Issue 2 — Incorrect PV generating status and phantom power (P1)

### Current problem

At night, Growatt cards may show:

- PV1 voltage: 0 V.
- PV input power: 1 W.
- Green "PV generating" status.

This is misleading.

Very small nonzero readings may be measurement noise, residual power or reporting artifacts.

### Required behavior

Introduce appropriate PV display normalization and generation-status thresholds.

Initial requirements:

- PV power between 0 and 10 W inclusive should display as 0 W.
- Negative invalid values should not silently be treated as valid production.
- The system should not report "PV generating" for 0–10 W.
- Generation status should activate above 20 W.
- Use hysteresis between activation and deactivation to prevent flickering.

Suggested state behavior:

- Activate Generating when valid PV power exceeds 20 W.
- Remain Generating until valid PV power falls to 10 W or below.
- Between 10 and 20 W retain the previous confirmed state, if known.
- When no prior state exists, use a conservative non-generating or indeterminate state according to the existing model.

Investigate whether voltage should be an additional validity condition.

If voltage is confirmed at 0 V and power reports 1 W, the status must not be Generating.

Do not invent a universal voltage threshold across inverter models without checking provider semantics.

### Raw telemetry preservation

Keep raw measurements unchanged in MySQL.

Do not rewrite historical telemetry.

Apply display normalization at the appropriate presentation or derived-metric layer.

The original 1 W should remain available to diagnostics and raw history if currently supported.

### Aggregation considerations

Investigate whether the 10 W display threshold should apply only to individual inputs or also to an installation-level aggregate.

Avoid applying the threshold multiple times in ways that create incorrect total energy calculations.

Do not silently zero-out legitimate small energy values in historical calculations.

Distinguish:

- Raw power.
- Displayed power.
- Generated/not-generating state.
- Accumulated energy.

### Tests

Include:

- 0 W.
- 1 W.
- 5 W.
- 10 W.
- 11 W.
- 15 W.
- 20 W.
- 21 W.
- 100 W.
- Null power.
- Invalid negative power.
- 0 V with 1 W.
- Stale telemetry.
- Transition through hysteresis.
- Master/Slave independently reporting generation.

---

## Issue 3 — Devices search does not filter results (P1)

### Reproduction

Navigate to Devices.

Enter:

`cook`

The logical device list remains unchanged.

For example, "Boiler Sturgel" continues to appear even when it does not match the search.

### Required behavior

Investigate and fix the search functionality.

Search must filter the currently accessible device inventory.

Include:

- Logical devices.
- Physical/provider devices.
- User-defined device names.
- Provider device names.
- Models.
- Manufacturers/providers.
- Device identifiers where appropriate.
- Room names when assigned.

Search must be:

- Case-insensitive.
- Whitespace-trimmed.
- Responsive while typing.
- Consistent with the selected property and access permissions.

Do not require pressing Enter unless the existing UX deliberately uses submit-based search.

Prefer the existing frontend search/data-loading architecture.

### Logical and physical devices

The interface contains both logical devices and provider devices.

Ensure the search applies consistently to all device sections.

For a nonmatching query:

- Hide unrelated device cards.
- Hide or collapse empty sections appropriately.
- Display a clear "No matching devices" state when no results exist.

Do not silently show all devices when the query matches nothing.

### Additional cases

Verify:

- Search by "cook".
- Search by "boiler".
- Search by "heatpump".
- Case differences.
- Partial strings.
- Leading/trailing whitespace.
- Custom names.
- Equal names across devices.
- Clearing the query.
- Changing property.
- Data refresh through SignalR.
- Empty device list.
- Multiple logical/physical representations of the same equipment.

Do not expose devices from unauthorized properties.

Ensure the search does not unnecessarily call provider APIs or reload telemetry for every keystroke.

If filtering is client-side, preserve the current result set and avoid stale-state bugs.

If filtering is server-side, ensure all relevant sections use the query consistently.

---

## Issue 4 — Solar installation summary shows Unavailable (P1/P2)

### Current problem

The Live Growatt page contains a "Solar installation" summary with four values:

- PV production — Unavailable.
- Household consumption — Unavailable.
- Grid availability — Unavailable.
- Grid import — Unavailable.

However, the Growatt Master and Slave cards below contain valid individual telemetry.

Previously observed examples include:

- Master PV input power.
- Slave PV input power.
- Master and Slave output/load power.
- AC input voltage around 243–244 V.
- Grid import values reported per inverter.
- Shared battery SOC and voltage.

The summary remains Unavailable despite those individual readings.

### Investigation

Trace the complete data flow from:

1. Growatt V4 provider payload.
2. Telemetry normalization.
3. Device capabilities.
4. Installation and device associations.
5. Latest readings.
6. Installation aggregate calculation.
7. Backend API.
8. React Solar installation summary.

Determine whether the problem originates from:

- Missing signal-to-installation mapping.
- Incorrect capability identifiers.
- Missing aggregation configuration.
- Incorrect property/installation bindings.
- Data freshness filtering.
- Master/Slave device classification.
- Incorrect data type or unit conversion.
- Frontend lookup or state handling.
- Missing or unsupported aggregate calculations.

Identify the exact cause for each of the four fields.

Do not treat all four as necessarily having the same root cause.

### PV production

For the two Growatt SPF inverters operating in parallel:

Investigate whether the available PV input power readings can be safely aggregated as an installation-level instantaneous PV production value.

Avoid double counting.

Distinguish PV power (W) from accumulated PV energy (kWh).

Only aggregate compatible measurements with acceptable timestamps.

Expose incomplete/partial aggregation explicitly rather than producing a misleading total.

### Household consumption

Investigate actual inverter output/load telemetry.

Determine whether installation-level household consumption can be calculated accurately by combining the two inverters.

Consider:

- Master/Slave parallel configuration.
- Output/load power.
- Battery charging.
- Grid charging.
- Inverter self-consumption.
- Unmetered loads, if any.

Do not assume that inverter output equals all property consumption under every operating topology.

Choose accurate labels and describe the metric's scope.

Do not fabricate measurements.

### Grid availability

Grid availability must not be confused with grid import.

An inverter may report:

- AC input voltage: 243 V.
- Grid import power: 0 W.

This means grid voltage is present while the inverter is importing no power.

It does not mean the external grid is unavailable.

If fresh, reliable AC input measurements support detecting grid presence, implement a clear derived grid availability state using the existing signal model.

Use appropriate source validation and consider Master/Slave disagreement.

Handle:

- Normal grid voltage.
- Confirmed zero voltage.
- Stale readings.
- Unknown state.
- Different readings from parallel devices.

Reuse or align with the Event Rule Engine's grid outage concepts where practical.

Do not create a second conflicting grid outage detection algorithm.

### Grid import

Investigate whether reported grid import metrics are suitable for safe aggregation.

Zero is a valid measurement.

A valid 0 W value must display as 0 W, not Unavailable.

If data truly cannot be aggregated, show Unavailable with a useful reason.

Avoid double counting shared measurements.

### Missing or unsupported metrics

Do not replace missing data with invented numbers.

If a metric is unsupported or lacks the necessary configuration:

- Keep it Unavailable.
- Explain why.
- Provide actionable diagnostic information if appropriate.
- Avoid exposing low-level implementation details in the primary UI.

Do not silently switch to unrelated measurements.

### Frontend presentation

The Solar installation section should clearly distinguish:

- Valid zero.
- Valid positive value.
- Partially available value.
- Stale value.
- Unsupported/unavailable value.

Use existing status and unit conventions.

Keep the UI compact.

If appropriate, show tooltips or a small diagnostic explanation for unavailable aggregates.

---

## Cross-cutting requirements

### Architectural consistency

Reuse the existing:

- Capability model.
- Provider adapters.
- Device identity model.
- Installation model.
- Telemetry freshness model.
- Latest readings infrastructure.
- SignalR.
- Notification Rule Engine.
- Property authorization.

Do not introduce multiple conflicting definitions of device availability, PV generation or grid availability.

Where monitoring and alerting need different concepts, define the distinction explicitly.

### Backward compatibility

Existing telemetry samples must remain unchanged.

Existing rule definitions and Configuration Vault profiles must continue to work.

Do not change provider identifiers without a migration and compatibility plan.

Do not modify historical energy data.

### Performance

Avoid full-history queries during normal dashboard refreshes.

Use latest readings and existing efficient aggregation paths where possible.

Prevent unnecessary provider requests triggered by Devices search.

Handle properties with many devices.

### Documentation

Update the relevant monitoring, telemetry, provider and frontend documentation.

Document:

- Online/freshness calculation.
- Provider-reported status vs application status.
- PV display thresholds and hysteresis.
- Device search behavior.
- Solar installation aggregation formulas and limitations.
- Grid availability semantics.

Update the project handoff and archive this prompt using the next available prompt number.

---

## Testing

Add backend and frontend regression tests covering all four issues.

### Backend

- Dynamic 1.5× polling freshness window.
- Configured polling intervals.
- Provider-reported offline with fresh data.
- Stale and unknown states.
- PV threshold behavior.
- PV hysteresis.
- Multi-inverter aggregation.
- Valid zero grid import.
- Grid voltage present with zero import.
- Missing/stale installation metrics.
- Alert Rule Engine compatibility.

### Frontend

- Growatt online status.
- PV generating badge.
- PV power display normalization.
- Devices search filtering.
- Logical device search.
- Physical device search.
- Search empty states.
- Solar installation values.
- Zero vs unavailable rendering.
- Property changes.
- SignalR refresh behavior.

Use isolated test data and disposable test databases.

Run the existing backend and frontend regression suites.

Run production builds.

Report test counts and failures.

---

## Constraints

- Do not modify or reset existing Configuration Vault credentials.
- Do not modify production or development database records as part of implementation or testing.
- Preserve existing OAuth flow behavior except for the requested safety improvements.
- Follow project architecture and UI conventions.
- Report any potentially destructive existing backend behavior before changing it.

Implement the change, add tests, and create a local commit. Do not push.

---

## Final Report

Provide:

1. Root cause of each of the four issues.
2. Changed backend components.
3. Changed frontend components.
4. Exact online/freshness calculation.
5. PV threshold and hysteresis implementation.
6. Devices search behavior.
7. Solar installation aggregation formulas and limitations.
8. Interaction with existing Notification Rule Engine.
9. Regression test results.
10. Build results.
11. Known limitations.
12. Any manual deployment steps.
13. Local Git commit hash.

Explicitly confirm that existing credentials, OAuth behavior and real database records were not modified, and that nothing was pushed.

## Execution summary

### Root causes

1. **Growatt shown Offline with fresh data.** `MonitoringReader` displayed the provider's `device_state.connectivity` whenever the status row was within `Growatt:FreshnessSeconds` (900 s), and ignored the readings.
   - The cloud adapter maps V4 `lost: true` to Offline, and earlier live checks found `lost` set on both inverters while their data was current.
   - The alert Connectivity signal read the same raw value, so a "device offline" rule would have opened incidents for communicating inverters.
2. **PV generating at 1 W.** `PvInput` treated any fresh power above 0 W as generating, ignored a 0 V PV voltage, and had no display threshold or hysteresis.
3. **Search not filtering.** `Devices.tsx` applied the search only to provider device cards through `DeviceCardOrder visible`.
   - `LogicalDevices`, the hardware tab and **Devices in your other properties** never received the query.
   - The empty state counted only provider devices.
   - Matching covered only name, manufacturer and model.
4. **Solar installation Unavailable.** The summary looked up installation-level series, and the V4 adapter returns `GrowattSnapshot(inverters, [])`. Each field had its own gap:
   - **PV:** an installation series existed only with `SumIndependentPv=true` (default false) and exactly equal timestamps.
   - **Household:** this needed `MasterReportsWholeSiteLoad=true` and a `HouseholdPower` reading that V4 never produces.
   - **Grid availability:** no source produces it, by design.
   - **Grid import:** the summary asked for `GridImportPower`, while V4 reports only per-inverter `AcInputPower`.

### Changes

- **Domain** (`DerivedState.cs`, new): `Availability`, `DeviceAvailability`, `PvGeneration`, `GridSupply`, `InstallationSummary`. In `Alerts.cs`, connectivity conditions accept Stale.
- **Application:** `DeviceDetails` gains `availability`, `availabilityBasis`, `reportedConnectivity`, `reportedAt`, `latestReadingAt`, `availabilityWindowSeconds` and `pvInputs`; `PropertyState` gains `solar`. All new fields are optional.
- **Infrastructure:**
  - `TelemetryHoldPolicy.AvailabilityWindow` and `Defaults`.
  - `MonitoringReader`: availability per device; for Growatt, PV state from a bounded 3-hour, 12-sample read, and `Summarize`.
  - `AlertSignalReader`: availability for the Connectivity signal and the catalog, and the "reported offline (provider)" label.
  - `AlertEvaluator` and `AlertRuleStore` take an optional hold policy.
  - `AlertTemplates` uses the `GridSupply` constants.
- **Frontend:**
  - `shared/search.ts` (new), `displayNumber` in `shared/utils.ts`, new types.
  - `Devices.tsx`: availability badge and tooltip, PV formatting, search across every section with one empty state.
  - `LogicalDevices.tsx` and `DeviceManagement.tsx`: search filters.
  - `GrowattPanel.tsx`: the installation summary, the PV badge from the server state, and the provider-offline diagnostic.
  - `DashboardWidgets.tsx`: PV display.
  - `styles.css`.
- **Docs:** [architecture 18](../architecture/18-availability-pv-installation-summary.md) (new), architecture 03 and 17, the Growatt guide, the README and the handoff.

### Verification

- **Backend:** Release build with zero warnings. With disposable MySQL 8.0.46 fixtures, 394 passed and 11 skipped, twice in a row; without fixtures, 345 passed and 60 skipped.
- **Frontend:** the production build passed and Vitest reported 157 passes.
- **New tests:** 36 pure backend cases, 1 MySQL test and 12 frontend tests.
- **Updated tests:** 4 existing assertions changed wording. No migration was added.

### Not touched

No live or development database, vault credential, OAuth flow, running backend or provider was touched. Nothing was pushed.

### Destructive behavior

No potentially destructive existing behavior was found or changed.
