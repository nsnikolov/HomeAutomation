# HomeAutomation — Milestone 7C: Historical Charts, Energy Visualization and Device Comparisons

Date: 2026-11-26

## Original prompt

# HomeAutomation — Milestone 7C: Historical Charts, Energy Visualization and Device Comparisons

Implement Milestone 7C of HomeAutomation: an interactive historical telemetry interface for temperatures, humidity, electrical power, energy consumption and other supported measurements.

The objective is to make the historical data infrastructure delivered in Milestones 7A and 7B accessible, understandable and useful through the existing React application.

This is a production-oriented implementation, not a charting prototype.

Use extended/high reasoning for architecture, historical-data correctness, query performance and frontend design.

## 0. Project context

Before implementation:

1. Read `CLAUDE.md`, `AGENTS.md` and `docs/AI_HANDOFF.md`.
2. Review existing architecture documents and ADRs.
3. Read `docs/architecture/10-historical-telemetry.md`.
4. Read `docs/architecture/11-logical-devices.md`.
5. Inspect the current React application, routing, dashboard, Devices and Energy pages.
6. Inspect the history APIs implemented in 7A and 7B.
7. Inspect the existing frontend styling, charting dependencies, reusable components and API client conventions.
8. Inspect recent commits and current Git status.

Relevant milestones:

- Milestone 7A: commit `754615e`.
- Milestone 7B: commit `35838fa`.
- Migration 017: historical telemetry aggregation.
- Migration 018: logical devices and replacement history.

Migrations 017 and 018 may not yet be applied to the live development database.

Do not assume the new backend functionality is deployed.

Preserve the existing .NET 10, Dapper, MySQL, React, TypeScript, SignalR and Configuration Vault architecture.

Produce a concise implementation plan before modifying code.

## 1. Main objective

Create a History section where users can explore historical measurements from devices in the selected property.

Supported initial measurements:

- Temperature (°C).
- Relative humidity (%).
- Electrical power (W).
- Electrical energy (kWh).
- Voltage (V).
- Current (A).
- Relay ON/OFF state and supported ON duration.
- Other existing metrics when their historical data semantics are reliable.

History must be based primarily on logical devices.

Users should see the equipment or measurement location, not be required to understand provider IDs, telemetry series IDs or physical hardware replacements.

Examples:

- Living Room Temperature.
- Outdoor Temperature.
- Heat Pump Power Meter.
- Boiler Power Relay.
- Bedroom Temperature and Humidity.

A physical hardware replacement must not split the user's historical view into unrelated charts.

The history view must preserve measurement provenance and show appropriate discontinuities where data is unavailable.

## 2. Navigation and user experience

Add a dedicated "History" entry to the existing main navigation.

Use the established application layout and styling.

The History page should include:

- Property context.
- Logical device selector.
- Measurement selector.
- Time-range selector.
- Resolution selection where relevant.
- Main historical chart.
- Summary statistics.
- Data coverage and quality information.
- Optional comparison with other compatible logical devices.

Provide convenient navigation to history from an individual logical device in the Devices page.

Preserve existing Devices, Dashboard, Energy, Growatt, Daikin, eWeLink and Tuya pages.

Do not redesign the application shell.

Use accessible labels, readable typography and consistent units.

Make the interface usable on desktop, tablets and mobile screens.

## 3. Time ranges

Support the following presets:

- Last 24 hours.
- Last 7 days.
- Last 30 days.
- Last 12 months.
- Custom date and time range.

Use the property's configured time zone when presenting dates and calendar boundaries.

For each selected range, choose an appropriate data resolution.

Suggested defaults:

| Range | Resolution |
|---|---|
| 24 hours | Raw or hourly |
| 7 days | Hourly |
| 30 days | Daily |
| 12 months | Monthly visualization derived from daily data |
| Custom | Automatic, based on requested range and available data |

Allow an explicit resolution override only where the API supports it and the resulting request remains bounded.

Avoid rendering thousands of points unnecessarily.

Never assume missing historical data means zero.

### Monthly aggregation

Milestone 7A stores hourly aggregates and calculates calendar-day summaries when reading.

Do not introduce a new monthly persistence table simply to draw annual charts.

If monthly values are required, calculate them accurately from available daily summaries or add a small bounded backend aggregation endpoint if justified.

Monthly averages must be weighted using valid coverage.

Monthly energy totals must preserve provenance, incomplete periods and measured-versus-estimated distinctions.

A month with incomplete coverage must not be presented as a fully measured total.

## 4. Main historical chart

Build an interactive time-series chart.

Required behavior:

- Clear x-axis with localized dates/times.
- Correct measurement units on the y-axis.
- Responsive sizing.
- Tooltips with timestamp, value and quality details.
- Clear legend.
- Appropriate zoom or range selection.
- Readable handling of large and small values.
- Loading, empty, unavailable and error states.
- Correct behavior when only one or two data points exist.

Use an existing charting library if available and suitable.

If none exists, choose a maintained React-compatible chart library with a reasonable bundle impact. Document the choice.

Do not build a custom charting engine unnecessarily.

### Missing data

Never connect lines across unsupported telemetry gaps.

Use actual null/missing points or explicit gap intervals.

Distinguish at least:

- No reading reported.
- Incomplete coverage.
- Period not yet aggregated.
- Measurement unsupported by the active physical device.

Do not interpolate missing temperatures, power or energy merely for a visually smooth graph.

The chart must reflect the backend's actual data-quality semantics.

## 5. Temperature history

For temperature measurements, display:

- Historical values.
- Average temperature.
- Minimum temperature.
- Maximum temperature.
- Valid sample count or data coverage where appropriate.

For hourly and daily data, support a min/max visualization in addition to the average.

Prefer a subtle shaded min/max band or another clear representation.

Users should be able to distinguish:

- Average.
- Minimum.
- Maximum.

For a daily view, the min/max values must represent the actual observed data, not an average of hourly min/max values.

Use backend-provided statistics instead of recalculating them incorrectly in the browser.

Example:

Living Room Temperature

- Average: 20.3 °C.
- Minimum: 18.1 °C.
- Maximum: 22.4 °C.
- Coverage: 96%.

The numbers above are illustrative only; use actual API responses in production.

## 6. Humidity history

Support the same visualization principles as temperature:

- Historical humidity.
- Average humidity.
- Minimum and maximum.
- Coverage and gaps.

Use percentages consistently.

Avoid presenting unsupported precision.

Allow temperature and humidity to appear together in a device detail view, but use separate axes or charts when units differ.

Never put different physical units on one unlabeled axis.

## 7. Electrical power history

For power-monitoring devices, show power in watts over time.

Support:

- Raw or time-aggregated power.
- Minimum, maximum and average power.
- Time-weighted averages where provided.
- Missing telemetry intervals.
- Optional peak-power statistics.

Examples:

- Heat pump electrical consumption.
- Boiler power relay.
- Other Tongou/Sonoff monitoring devices.

Show periods where data is missing instead of displaying the last observed value indefinitely.

Do not infer a device is consuming zero watts because it stopped reporting.

For signed metrics, preserve the distinction between import/export or charge/discharge where the backend exposes it.

## 8. Electrical energy history

Energy visualization is a central feature.

Support energy consumption charts showing kWh per:

- Hour.
- Day.
- Month.

For appropriate ranges, use bar charts for interval energy.

Important distinctions:

### Measured energy

Energy derived from supported device counters.

### Estimated energy

Energy calculated by integrating power readings over sufficiently covered periods.

Display these separately or with unmistakably different visual treatment.

Do not silently add measured and estimated energy into one authoritative number.

If an interval has incomplete coverage, indicate that the displayed value is partial or estimated according to the API provenance.

Do not suggest it represents complete actual consumption.

### Energy counters and replacements

Never subtract cumulative counter readings across two different physical devices.

For logical devices whose physical hardware was replaced, aggregate only valid intervals from each physical assignment.

Preserve counter resets and quality flags.

### Growatt

Preserve the existing Growatt master/slave and installation-level semantics.

Do not produce property-wide totals by blindly summing inverter, battery and installation measurements.

Do not introduce new accounting logic without validating the existing backend semantics.

If an accurate total is unavailable, display the available individual series instead.

## 9. Relay history

Where reliable relay-state telemetry is available, provide a simple state history.

Options include:

- ON/OFF timeline.
- Supported ON duration per hour or day.
- Coverage indicator.

Choose the simplest visualization supported by the data.

Show unsupported periods clearly.

Do not assume a relay remained ON indefinitely after communication was lost.

Do not introduce physical relay-control functionality.

## 10. Device replacement markers

Milestone 7B stores assignment history for logical devices.

Use that information to show physical-device replacement events on historical charts.

For example:

- Before 26 Nov 2026, 14:37 — Tuya Sensor A.
- From 26 Nov 2026, 14:37 — eWeLink Sensor B.

Requirements:

- Display a replacement marker at the correct timestamp.
- Show old and new physical-device identities in a tooltip or details view.
- Preserve the continuous logical measurement history.
- Do not draw fabricated connections across gaps.
- Respect arbitrary replacement timestamps, including replacements within an hour.
- Handle multiple replacements within the selected time range.

Use assignment metadata returned by the backend.

Do not infer replacements merely from changes in provider name.

## 11. Multiple-device comparison

Allow users to compare historical measurements from multiple logical devices.

Initial target: at least 2–4 logical devices.

Examples:

- Living room vs outdoor temperature.
- Bedroom vs living room temperature.
- Heat pump vs boiler power.
- Two room humidity sensors.

Requirements:

- Select compatible logical devices.
- Compare the same canonical metric and compatible units.
- Use a common time axis.
- Use distinct, readable series styles.
- Show an understandable legend.
- Preserve each series' missing-data gaps.
- Keep data coverage and quality independently visible.
- Avoid merging observations from unrelated devices.

Do not add together power or energy values merely because they are displayed in the same comparison.

If the comparison requires incompatible units, reject it or use clearly separated charts.

A comparison should be optional; the default view remains simple.

## 12. Summary statistics

Above or below the selected chart, show relevant summary values.

For temperature and humidity:

- Average.
- Minimum.
- Maximum.
- Coverage.

For power:

- Average W.
- Maximum W.
- Estimated energy when supported.
- Coverage.

For energy:

- Measured kWh.
- Estimated kWh.
- Coverage or completeness.

For relay state:

- Supported ON duration.
- Covered duration.

Summary statistics must represent the selected time range.

Do not calculate a misleading average of hourly averages.

Use backend aggregates or properly weighted calculations.

A partial interval must not be silently treated as complete.

## 13. Data coverage and quality

Milestone 7A exposes coverage and quality metadata.

Use it throughout the History UI.

Requirements:

- Display coverage percentage when meaningful.
- Mark periods below the established 80% coverage threshold as incomplete.
- Distinguish unavailable measurements from zero-valued measurements.
- Distinguish unaggregated time ranges from measured zero.
- Explain estimated energy.
- Preserve backend provenance.
- Avoid presenting inaccurate summary totals.

Provide concise explanatory tooltips for quality states.

Do not overwhelm the main chart with warnings.

### eWeLink limitations

Existing eWeLink power telemetry may have poor coverage because some electrical readings depend on live-view activity.

Do not conceal this issue or compensate for it by inventing values.

Display the actual available history and its coverage.

Continuous eWeLink power collection is a separate future milestone and must not be implemented here.

## 14. API integration

Reuse the existing 7A/7B historical endpoints.

Inspect their actual request/response contracts before implementation.

Prefer using:

- Property-authorized logical-device history.
- Existing raw/hourly/daily resolutions.
- Existing assignment history.
- Existing coverage and quality metadata.

Do not create a second history API without justification.

If the API lacks a small capability required for correct visualization, implement the minimal compatible backend extension and corresponding tests.

Keep historical requests bounded.

Avoid loading all raw observations for a full year.

Prefer aggregate resolutions for longer ranges.

Use existing frontend API client, authentication, error handling and state-management conventions.

Avoid duplicate requests and race conditions when users change devices or date ranges quickly.

## 15. Deployment compatibility

Migrations 017 and 018 may not yet be applied to the running development database.

The frontend must handle unavailable history APIs gracefully.

Requirements:

- Do not crash if history infrastructure is not yet deployed.
- Display an appropriate "History is not available yet" state when the backend reports missing functionality.
- Distinguish this from "No historical readings".
- Avoid misleading empty charts during a backend/frontend version mismatch.
- Preserve all existing application functionality.

Do not apply database migrations in this milestone.

Do not restart the running application.

Do not run history backfill or logical-device initialization against live data.

Do not change the existing database or Configuration Vault.

## 16. Performance

The target deployment initially contains a relatively small number of properties and devices, but history will grow over multiple years.

Requirements:

- Avoid unbounded history requests.
- Use appropriate history resolution.
- Avoid rendering excessive chart points.
- Keep component re-renders reasonable.
- Use cancellation or stale-response protection when filters change.
- Avoid unnecessarily loading all logical devices' history.
- Preserve smooth chart interactions.

Prefer straightforward solutions over premature complexity.

Do not introduce a separate analytics backend or time-series database.

## 17. Accessibility and design

Use the existing HomeAutomation visual language.

Requirements:

- Consistent spacing and typography.
- Responsive layout.
- Legible axis labels.
- Accessible controls.
- Keyboard-operable filters.
- Color-independent distinction of measured vs estimated values.
- Clear chart legends.
- Useful empty states.
- Support for narrow screens.

Do not hardcode English-only assumptions into date/time formatting.

Use a reusable date/time formatting approach respecting the property's time zone.

Handle DST ambiguity in tooltips when local clock times repeat.

## 18. Testing

Implement frontend tests covering at least:

### Page and navigation
- History route.
- Logical device selection.
- Metric selection.
- Time range selection.
- Custom date range.
- Loading and empty states.
- API failure states.

### Charts
- Raw data rendering.
- Hourly and daily data.
- Min/max display.
- Missing-data gaps.
- Zero versus missing value.
- Incomplete coverage.
- Unaggregated periods.
- Single-point series.
- Multiple comparison series.

### Energy
- Measured versus estimated energy.
- Hourly/daily/monthly energy.
- Partial coverage.
- No accidental double-counting.
- Different energy provenance.

### Logical devices
- History spanning a physical replacement.
- Multiple device replacements.
- Replacement within an hour.
- Replacement markers.
- Missing capabilities after replacement.

### Time zones
- Property-local formatting.
- DST transitions.
- 23-hour and 25-hour days.
- Non-whole-hour UTC offsets.
- Custom date-range boundaries.

### Compatibility
- Existing dashboard.
- Devices page.
- Energy page.
- Existing provider pages.
- History API unavailable before migration/deployment.

Add backend tests only for necessary API changes.

Use mocked API responses and synthetic telemetry fixtures.

Do not contact live providers or control any devices.

## 19. Scope boundaries

This milestone includes:

- History navigation and page.
- Individual logical-device historical charts.
- Temperature/humidity visualization.
- Power and energy visualization.
- Relevant relay-state history.
- Time-range selection.
- Coverage and provenance visualization.
- Device replacement markers.
- Simple multiple-device comparison.
- Relevant summary statistics.
- Minimal backend changes required for correct rendering.

Do not implement:

- Continuous eWeLink power polling.
- Automatic logical-device creation on device discovery.
- New provider integrations.
- Device pairing or control.
- Automation rules based on historical data.
- New database migrations unless an unavoidable, explicitly documented correctness requirement exists.
- Data retention or deletion.
- Historical data export.
- Complex report builders.
- Major dashboard redesign.
- Unrelated refactoring.

If the scope becomes too large, prioritize correct single-device history and energy visualization before advanced comparison features.

## 20. Implementation sequence

Implement in the following order:

1. Inspect existing history APIs and frontend components.
2. Finalize UX and component structure.
3. Implement History navigation and route.
4. Implement logical device and metric selection.
5. Implement time-range and resolution selection.
6. Implement core time-series charts.
7. Add temperature/humidity statistics.
8. Add power and energy charts.
9. Add coverage and quality indicators.
10. Add device replacement markers.
11. Add multiple-device comparisons.
12. Implement required tests.
13. Update documentation and handoff.

Keep the implementation modular and avoid large monolithic components.

Prefer reusable visualization components with typed data models.

## 21. Safety

Do not:

- Apply migration 017 or 018 to the live database.
- Run backfill or logical-device initialization against live data.
- Restart the running backend.
- Call real provider APIs.
- Change provider polling intervals.
- Control physical devices.
- Modify Configuration Vault credentials.
- Push Git commits.

Tests must use synthetic fixtures and mocks or disposable databases where required.

## 22. Completion requirements

At completion:

1. Summarize the implemented UI functionality.
2. Explain the History page and navigation.
3. List supported metrics, periods and resolutions.
4. Describe replacement markers and logical-device continuity.
5. Explain energy provenance and incomplete-coverage presentation.
6. Report any minimal backend changes.
7. List any limitations and deferred features.
8. Report frontend production build results.
9. Report frontend test results.
10. Report backend build and tests if backend changes were made.
11. Update architecture or UI documentation.
12. Update `docs/AI_HANDOFF.md`.
13. Archive this prompt as the next numbered file under `docs/prompts`.
14. Commit locally with a descriptive message.
15. Do not push.

Do not proceed to additional milestones automatically.

The result should provide a reliable, attractive and practical historical telemetry interface without compromising the data integrity established by Milestones 7A and 7B.

## Execution summary

- Inspected the 7A and 7B history contracts at `35838fa`. Recharts 3 was already used by the Energy page, so no chart library was added. Two backwards-compatible fields were missing for correct rendering:
  - `metrics` on logical devices (canonical metrics instead of guessing from capabilities);
  - `maximumHoldSeconds` on logical history segments (raw line breaks).

  Both were added to `LogicalDeviceStore` and `TelemetryHistoryReader`, with MySQL and HTTP tests. No migration was needed.
- Added `src/frontend/home-automation-web/src/features/history/`:
  - `historyModel.ts`: property-zone conversion and formatting; bounded ranges and resolutions; weighted monthly and summary combination; gap-aware rows and raw breaks; replacement markers.
  - `HistoryCharts.tsx`: measurement, energy, relay and raw charts; quality strip; replacement list; accessible tables; summaries.
  - `HistoryPage.tsx`: URL-driven filters, custom range, comparison and availability states.
  - Tests and fixtures.
- Added the History navigation entry and lazy route, and the "View history" link on logical devices.
- Documented the page in `docs/architecture/12-history-charts.md`, the README and the handoff.
- Verification: the frontend production build passed (History is a 34 kB lazy chunk) and Vitest reported 82 passes. The backend Release build passed with zero warnings, and 265 tests passed with 11 opt-in skips on disposable MySQL 8.0.46. Nothing ran against the live database, the backend was not restarted, and no provider or device was contacted. Graphify is not installed, so `graphify update .` was not run.
