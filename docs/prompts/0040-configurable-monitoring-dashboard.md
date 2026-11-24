# HomeAutomation — Milestone 5A: Configurable Multi-Property Monitoring Dashboard

Date: 2026-11-24

## Original prompt

# HomeAutomation — Milestone 5A: Configurable Multi-Property Monitoring Dashboard

Implement a configurable, compact, read-only monitoring dashboard with user-defined groups, widgets and measurement values.

This milestone replaces the fixed, simulator-oriented main Dashboard with a real-data dashboard configurable through the UI.

## Project context

- Backend: .NET 10, ASP.NET Core, Dapper, MySQL.
- Frontend: React, TypeScript, Vite.
- Existing SignalR updates, authentication and property-scoped authorization.
- Provider integrations: Growatt, Daikin ONECTA, eWeLink and Tuya.
- Existing Property, Room, Device and Capability concepts.
- First configured property: Countryside House (Sturgel).
- Additional properties will be added later.

Inspect existing architecture, schemas, telemetry queries and authorization before implementing. Reuse existing abstractions rather than introducing a separate device registry.

## 1. Core behavior

The dashboard is strictly MONITORING ONLY.

No ON/OFF buttons, temperature adjustment, relay switching or provider commands are allowed directly from dashboard widgets.

Clicking a monitoring widget navigates to its existing device/provider detail page where controls are available.

Navigation must be an authorized internal route, never an arbitrary externally supplied URL.

Keep the existing provider pages unchanged.

## 2. Configuration hierarchy

Use this hierarchy:

Property
  → Dashboard Groups
    → Widgets
      → Measurement Values

Each property has its own dashboard configuration.

Dashboard Group:
- User-defined title.
- Sort order.
- Collapsed/expanded state.
- Optional visibility setting.

Widget:
- User-defined title.
- Parent group.
- Sort order.
- Compact or Detailed display mode.
- One or more measurement values.
- Optional internal navigation target derived from an assigned device/provider.

Measurement Value:
- Source device.
- Specific registered capability or telemetry measurement.
- Optional user-defined display label.
- Unit and formatting from capability metadata.
- Value and observation timestamp.
- Timestamp provenance, if available.

A widget can contain multiple values from the same or different devices.

Example:
Group: Climate
Widget: Kitchen AC
Values: Daikin room temperature, target temperature, operating mode.

Group: Climate
Widget: Kitchen Thermometer
Values: Tuya temperature, humidity.

Group: Electrical
Widget: Boiler
Values: eWeLink switch ON/OFF and instantaneous power.

Group: Inverter
Widget: Energy Overview
Values: Growatt PV power, AC input power and signed battery power.

Do not hardcode these widget names or specific device IDs.

## 3. Configuration editor

Provide a simple configuration UI allowing an authorized user to:

- Create, rename and delete groups.
- Create, rename and delete widgets.
- Add, remove and reorder measurement values within a widget.
- Select a source through Property → Provider → Device → Measurement/Capability.
- Choose Compact or Detailed widget presentation.
- Reorder groups and widgets using accessible Up/Down controls; reuse existing drag-and-drop mechanisms when practical.
- Configure the internal navigation destination using the widget's selected source device.
- Preview the widget before saving.
- Save configuration persistently to MySQL.

Ensure all source selections and navigation destinations belong to the current property and are authorized server-side.

Avoid a complicated drag-and-drop dashboard designer in this milestone.

## 4. Automatic collapsible views

Both Dashboard Groups and Widgets must support independent expansion/collapse.

Default behavior:
- Display content fully when it occupies two rendered rows or fewer.
- When content would occupy more than two rows, initially display only two rows and show an accessible "Show all / Collapse" control.
- Expanded content displays all configured items.
- Allow manual collapsing/expansion.
- Do not show unnecessary expand/collapse controls when everything already fits.

For groups, rows contain widgets.
For widgets, rows contain measurement values.

Important:
- Determine row count using actual responsive layout, not a fixed number of items.
- Handle desktop, tablet and mobile layouts.
- Avoid layout jumping and clipping important values.
- Keep controls keyboard-accessible with correct ARIA expanded state.
- Prefer a straightforward, predictable implementation over complex measurement or animation logic.

Preserve expanded/collapsed state during live telemetry updates. Optional client-side persistence of expansion preferences is acceptable, but do not trigger database writes on every toggle.

## 5. Measurement freshness colors

Each displayed value has its OWN freshness state, based on its actual observation timestamp.

- GREEN: age < 5 minutes.
- BLUE: age >= 5 minutes and < 60 minutes.
- GRAY: age >= 60 minutes.
- Unavailable: no valid reading has ever been received, invalid value or inaccessible source. Display "—", never invent zero.

At exactly 5 minutes, use BLUE.
At exactly 60 minutes, use GRAY.

Valid zero is a valid measurement.

Use provider observation timestamps when available; otherwise use explicitly recorded server-received timestamps.

Do not use frontend refresh time or the latest provider poll time to make old measurements appear fresh.

Show full timestamp and source/provenance in tooltip or accessible details.

Recalculate age as time passes, even without new telemetry messages.

Use the requested colors for numeric and textual values (ON/OFF, mode, etc.). Preserve readability and accessibility; use text or tooltips so color is not the only freshness indicator.

## 6. Dashboard rendering

- Compact, information-dense and responsive.
- User-defined group order.
- User-defined widget order within each group.
- Multiple measurements per widget.
- Avoid large cards for simple one-value widgets.
- Support reasonable desktop grid layouts and compact mobile rendering.
- Make widget titles clearly distinguishable from their values.
- Preserve exact numeric units, precision and sign conventions.
- No fabricated summary values.
- No automatic aggregation across multiple devices in this milestone.
- No device commands from the dashboard.

Clicking a widget should navigate to its configured control/detail page without changing device state.

Separate configuration actions and collapse controls from navigation so these interactions never trigger navigation accidentally.

## 7. Backend and persistence

- Add minimal MySQL migrations for groups, widgets and value bindings.
- Implement property-scoped CRUD APIs with permission checks.
- Enforce cross-property isolation for groups, widgets, device sources and value bindings.
- Read displayed values from existing persisted telemetry and provider projections.
- Prefer a single efficient dashboard snapshot API over an N+1 query per widget.
- Reuse existing SignalR notifications for live updates.
- Avoid direct Growatt, Daikin, eWeLink or Tuya API requests from dashboard loading or editing.
- Preserve existing provider polling limits, backoff, and write safety.

If a configured source disappears, retain the widget configuration and show Unavailable with an explanation.

Keep simulation data out of real dashboard measurements unless explicitly selected and clearly identified as simulated.

## 8. Initial state and migration

Do not automatically invent monitoring widgets or bind arbitrary devices.

For existing properties with no dashboard configuration, show a useful empty state and an "Configure dashboard" action.

Do not delete existing telemetry, providers, permissions, properties or device assignments.

Existing provider-specific pages must continue to work.

## 9. Tests

Backend:
- Property isolation and permissions.
- Group/widget CRUD and ordering.
- Valid and invalid source bindings.
- Multi-value widgets.
- Missing or deleted source devices.
- Snapshot and timestamp provenance.

Frontend:
- Green/blue/gray boundaries at 5 and 60 minutes.
- Valid zero versus unavailable.
- Automatic collapse after two rendered rows.
- Expansion independent at group and widget level.
- Expanded state survives live updates.
- Responsive layouts.
- Widget navigation without device commands.
- Configuration creation and editing.

Run builds and relevant tests. Update documentation, archive this prompt and commit locally without pushing.

Do not implement historical charts, calculated metrics, automations or unrelated provider refactoring.

## Completion report

Summarize:
1. New database migration(s).
2. Dashboard configuration and editing capabilities.
3. Supported telemetry sources and any missing mappings.
4. Freshness and collapse behavior.
5. Tests executed.
6. Any remaining limitations.

## Execution summary

Replaced the fixed main dashboard with a shared, property-scoped monitoring layout. Migration 011 stores ordered groups, widgets and value bindings. The editor supports accessible ordering, compact and detailed previews, group visibility, and destinations resolved from assigned sources. Viewers can read the layout. Property operators, administrators and owners can edit it. Saves use CSRF protection and a revision check.

The snapshot reads assigned Growatt, Daikin ONECTA, eWeLink and Tuya data from MySQL. Explicitly selected simulator values are labeled. Missing sources retain their bindings and show unavailable. The dashboard uses each value's dated observation and provenance, ages values at five and sixty minutes, and preserves independently expanded groups and widgets across refreshes.

Verified migration application and reapplication, property isolation, persistence and provider projections on isolated MySQL 8.0.46. Both builds passed. Backend tests passed 163, with seven unrelated opt-in database tests skipped. Frontend tests passed 38. Headless Chrome fixture checks covered layout at widths of 1500, 1024 and 390 pixels, expansion through a data refresh, and internal navigation. The live schema and device configuration were left untouched.

- [Migration 011](../../database/migrations/011_monitoring_dashboard.sql)
- [Dashboard API](../../src/backend/HomeAutomation.Api/MonitoringDashboardEndpoints.cs)
- [Monitoring dashboard setup](../../README.md#configurable-monitoring-dashboard)
