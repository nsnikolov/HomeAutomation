# HomeAutomation — Milestone 5B: Configurable Dashboard Layout and Interactions

Date: 2026-11-24

## Original prompt

# HomeAutomation — Milestone 5B: Configurable Dashboard Layout and Interactions

Improve the existing configurable monitoring dashboard. Focus on usability, layout flexibility and measurement-level navigation.

**Use Medium reasoning. Prioritize efficient implementation and avoid unnecessary refactoring.**

## 0. Codebase exploration — Graphify

Before exploring the codebase:

- Check whether Graphify is installed and its project knowledge graph is available.
- If available, use Graphify to locate relevant dashboard components, configuration models, repositories, migrations and tests.
- Prefer targeted graph queries over repeatedly reading large source files.
- Verify findings against actual source code before editing.
- If the graph is outdated, refresh it only if inexpensive.
- If Graphify is unavailable, broken or incompatible, continue with normal targeted code searches.
- Do not install, troubleshoot or reconfigure Graphify as part of this task.
- Do not commit generated graph files.

Keep exploration focused on the existing monitoring dashboard.

## 1. Dashboard group behavior

Remove the expand/collapse chevron beside group titles (e.g. СТЪРГЕЛ).

Groups must:
- Always be expanded in monitoring mode.
- Display all their widgets.
- Preserve configured ordering and visibility.
- Support multiple groups per property.

Global Compact/Detailed controls must not hide groups.

Remove obsolete group-collapse presentation logic where safe, but avoid unrelated refactoring.

## 2. Widget expansion

All configured measurement values must be visible by default, regardless of their count.

- Do not initially truncate widgets to two measurement rows.
- Preserve manual Collapse / Show all functionality for large widgets.
- Global Detailed expands all widgets and switches them to Detailed.
- Global Compact switches all widgets to Compact without automatically hiding measurements.
- A manually collapsed widget remains collapsed during telemetry refreshes.
- After a global action, local widget controls remain independently usable.

Keep the behavior predictable and consistent.

## 3. Configurable widget width

Add a width setting to the existing dashboard configuration editor.

Use a responsive CSS Grid with configurable widget spans:

- 1 column
- 2 columns
- 3 columns
- 4 columns / full width

Widgets in the same group may have different widths.

Requirements:
- Persist the selected width.
- Automatically clamp spans to the available columns.
- Avoid horizontal scrolling and overlapping widgets.
- Mobile layouts should normally use one widget per row.
- Preserve existing widget ordering.
- Default existing widgets to their current approximate width.

Do not implement arbitrary pixel-based resizing or a complex visual layout designer.

## 4. Measurement columns within widgets

Add a separate setting for the number of measurement columns:

- 1
- 2
- 3
- 4

Example:
- House widget: 2 columns.
- Inverter widget: 3 columns.

This setting must be independent of widget width.

Use responsive behavior to prevent narrow measurement tiles and text overflow.

Persist the configuration.

## 5. Drag and drop measurement ordering

In Configure Dashboard mode, allow measurements to be reordered within their widget.

Requirements:
- Provide a small drag handle for every measurement.
- Support pointer-based drag and drop.
- Support keyboard-accessible Up/Down alternatives.
- Preserve measurement identity, source binding, custom label and configuration.
- Update order in the editor immediately.
- Persist changes only when Save Dashboard is pressed.
- Cancel must discard unsaved changes.
- Do not allow dragging during normal monitoring mode.

Reuse existing project drag-and-drop patterns or dependencies where possible.

Do not add a large new library unless necessary.

## 6. Custom colors for measurement values

Allow each measurement to use either:

A. Automatic freshness-based coloring (existing default).

B. A fixed custom color that overrides freshness coloring.

Provide a small preset palette:
- Green
- Blue
- Orange
- Red
- Purple
- Gray

Optionally allow a custom hexadecimal color.

Requirements:
- Persist the selected mode and color per measurement.
- Validate the color and check readability against the existing measurement tile background.
- Apply custom color only to the displayed measurement value.
- Keep labels, metadata and backgrounds unchanged.
- Preserve timestamps, stale classification and tooltip details even when a fixed color is used.
- Existing measurements must default to automatic freshness coloring.
- Missing or invalid measurements must display Unavailable / —, never a fabricated value.

Do not change the existing freshness thresholds:
- Green: under 5 minutes.
- Blue: 5 to under 60 minutes.
- Gray: 60 minutes or older.

## 7. Measurement-level navigation

Remove the navigation/link icon from widget headers.

Instead, clicking an individual measurement tile must open the corresponding source device/provider page.

Example:
- Daikin temperature → Daikin ONECTA device/page.
- Tuya thermometer → Tuya device/page.
- Growatt PV power → Growatt page.
- eWeLink relay power → eWeLink page.

A widget may contain measurements from several providers, so navigation must be determined individually for each measurement.

Requirements:
- Derive destinations from existing trusted provider/device routing logic.
- Never use arbitrary external URLs.
- Enforce property-scoped authorization.
- If a direct device route exists, prefer it; otherwise use the existing provider page.
- Make navigable measurement tiles keyboard accessible.
- Provide subtle hover and focus states.
- Never send device-control commands from the dashboard.
- If a source has no valid destination, keep the measurement non-clickable.

Remove obsolete widget-level navigation UI, but preserve source bindings.

## 8. Existing controls and behavior

Preserve:
- Global Compact / Detailed buttons.
- Individual widget Compact / Detailed icon toggles.
- Measurement freshness and timestamps.
- Existing property permissions.
- All provider-specific pages.
- Existing telemetry and SignalR refreshes.
- Read-only monitoring behavior.

Global Compact/Detailed must override local widget presentation modes while preserving measurement visibility according to section 2.

Local widget choices should survive telemetry updates.

Do not introduce new provider API requests.

## 9. Persistence and authorization

Reuse the existing property-scoped dashboard schema and APIs.

Add only the minimal migration(s) needed for:
- Widget width.
- Measurement column count.
- Measurement color mode and optional custom color.

Ensure:
- Existing saved dashboards continue to load.
- Existing configuration is migrated with sensible defaults.
- Property isolation is maintained.
- Viewer accounts cannot edit.
- Owner/Admin/Operator permissions remain unchanged.
- Database permissions needed by the new migration and runtime operations are documented.

Avoid unnecessary schema redesign.

## 10. Testing and verification

Add focused regression tests covering:

- Widget width and responsive column spans.
- Measurement column counts.
- Drag-and-drop ordering.
- Keyboard reordering.
- Custom and automatic measurement colors.
- Measurement-level navigation for mixed providers.
- Missing/deleted source devices.
- Global and local Compact/Detailed behavior.
- Default expanded measurement visibility.
- Property isolation and authorization.

Run relevant backend/frontend tests and builds.

Use headless Chrome to check desktop, tablet and mobile widths where practical.

Do not send any physical device commands.

## 11. Scope and completion

Do not implement:
- Historical charts.
- Calculated measurements or formulas.
- New device integrations.
- New automation rules.
- Provider polling changes.
- Unrelated refactoring.

If the task becomes too large, prioritize in this order:

1. Correct group and widget expansion behavior.
2. Configurable widget widths and measurement columns.
3. Measurement-level navigation.
4. Custom measurement colors.
5. Drag-and-drop reordering.

Implement as much as can be completed correctly, with tests. Do not leave partially applied database changes.

At completion:
- Report implemented features.
- List any unfinished requirements.
- Report migrations and whether the live database needs updating.
- Report test/build results.
- Archive this prompt under docs/prompts.
- Commit locally without pushing.

## Execution summary

Visible groups always show all widgets in saved order. Widgets start expanded, still support manual collapse, and keep that state on refresh. Global Compact preserves measurement visibility, while Detailed expands all widgets. Individual display toggles stay independent.

The editor now saves widget width and measurement columns separately, offers six contrast-checked fixed colors alongside automatic freshness, and supports pointer and keyboard measurement ordering in the draft. Individual tiles link to their assigned source through a property-scoped route. Legacy widget navigation and group collapse fields remain readable for compatibility.

Migrations 012 and 013 add the three layout and color settings. Isolated MySQL 8.0.46 verified the upgrade from migration 011, preserved an existing widget and binding, and supplied default width 1, measurement columns 2, and automatic color. The runtime account uses its existing dashboard grants. The migration account needs ALTER on the widget and measurement tables. The live database was not migrated.

Both builds passed. Backend tests passed 164 with eight opt-in tests skipped, and all 43 frontend tests passed. Headless Chrome verified responsive spans, measurement columns, custom color, tile links, and expansion at 1440, 820, and 390 pixels. The optional custom hexadecimal editor field was omitted.

- [Dashboard setup](../../README.md#configurable-monitoring-dashboard)
- [Widget layout migration](../../database/migrations/012_dashboard_widget_layout.sql)
- [Measurement color migration](../../database/migrations/013_dashboard_measurement_colors.sql)
