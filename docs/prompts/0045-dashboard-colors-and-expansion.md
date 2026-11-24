# Dashboard — Fix Actual Freshness Colors and Improve Expand/Collapse Controls

Date: 2026-11-24

## Original prompt

# Dashboard — Fix Actual Freshness Colors and Improve Expand/Collapse Controls

Improve the existing configurable monitoring Dashboard without changing its data model or provider integrations.

## 1. Fix stale measurement rendering

Observed in the live dashboard:

- Daikin Kitchen temperature: 18°C, fresh, green — correct.
- Daikin target: 16°C, fresh, green — correct.
- Tuya Kitchen thermometer: 16.5°C, explicitly classified as 60+ minutes old, but still visually blue/slate — incorrect.

The age calculation appears correct. Focus on actual rendering.



Requirements:

- Fresh (<5 min): visibly green.
- Intermediate (5–<60 min): visibly blue.
- Stale (>=60 min): neutral gray, not blue-tinted slate.
- Inspect actual computed CSS styles in headless Chrome, not only CSS class names.
- Verify styles in both Compact and Detailed modes.
- Use clear, distinguishable colors while preserving accessibility.
- Keep the measurement label and metadata neutral.
- Add a visual or computed-style regression test for stale readings.

## 2. Group-level expand/collapse buttons

Add a small chevron icon to every dashboard group header.



Behavior:

- Clicking the icon expands or collapses ONLY that group.
- Other groups and widgets remain unchanged.
- Groups initially show a compact preview when collapsed.
- Show a clear expanded/collapsed state with accessible labels and aria-expanded.
- The group title remains visible when collapsed.
- Clicking the icon must never navigate to a provider page.
- Preserve local group expansion preferences during telemetry refreshes.

Also preserve widget-level expansion where a widget has more than two rendered rows.

## 3. Global Compact / Detailed behavior

Keep the existing Compact and Detailed buttons in the Dashboard header.



Change their behavior:

- Compact: force ALL groups and widgets into compact/collapsed presentation.
- Detailed: force ALL groups and widgets into detailed/expanded presentation.
- Global actions override all previous per-group and per-widget expansion states.
- After a global action, individual groups and widgets can still be expanded/collapsed independently.
- Repeatedly clicking the same global mode should consistently apply that mode to all items, not restore an earlier mixed state.
- Do not modify the saved shared dashboard layout or widget configuration.
- Do not issue device commands or new provider API requests.

Ensure this works across desktop, tablet and mobile.

## 4. Verification

- Test multiple groups with independent expand/collapse state.
- Test global Compact and Detailed overriding mixed local states.
- Test local overrides after global actions.
- Verify value colors through browser computed styles.
- Preserve read-only navigation and existing measurement tooltips.
- Run relevant frontend tests and build.
- Archive prompt and commit locally without pushing.

Keep changes focused on dashboard presentation.

## Execution summary

Headless Chrome on the existing development origin computed neutral gray for the stale value in both modes. The surrounding measurement tile, label, and metadata still used slate tones. The updated tile and neutral text remove that cast, and stale values now use a stronger neutral gray. Added a computed-style regression using the dashboard stylesheet and kept the existing freshness and tooltip logic.

Added independent group chevrons with a one-row collapsed preview and kept the widget controls for measurements beyond two rows. Compact and Detailed reset local expansion every time they are pressed. Local choices thereafter survive telemetry refreshes. These actions change presentation without saving the shared layout.

The production frontend build and all 41 tests passed. Headless Chrome verified the production bundle in both modes at 1440, 820, and 390 pixels using dashboard fixtures. It confirmed green `rgb(8, 98, 74)`, blue `rgb(36, 91, 153)`, and gray `rgb(74, 74, 74)`, plus independent and repeated expansion actions. No provider or write requests were observed. The isolated browser fixture reported one inline fetch error per viewport with a stack outside the production bundle, while the dashboard API and control checks passed.

- [Dashboard presentation and expansion](../../src/frontend/home-automation-web/src/features/DashboardWidgets.tsx)
- [Computed colors and expansion regression tests](../../src/frontend/home-automation-web/src/features/Dashboard.test.tsx)
