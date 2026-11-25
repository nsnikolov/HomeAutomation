# HomeAutomation — Persist Dashboard Widget View State

Date: 2026-11-25

## Original prompt

# HomeAutomation — Persist Dashboard Widget View State

Improve the existing configurable Dashboard by remembering the last presentation state of each widget.



This is a small, frontend-focused enhancement. Reuse the existing dashboard state management and avoid unrelated refactoring.

## 1. Persist individual widget state

For each widget, remember independently:

- Presentation mode: `compact` or `detailed`.
- Measurement expansion state: `expanded` or `collapsed`.

These are independent properties.



Persist the state when the user:

- Switches a widget between Compact and Detailed.
- Clicks Collapse / Show all.
- Uses the global Compact or Detailed controls.

## 2. Restore saved state

Restore the last saved state when:

- The Dashboard is refreshed.
- The user navigates away and returns.
- The browser is closed and reopened.
- Telemetry updates arrive.
- The user switches between properties.

Do not reset local choices merely because the dashboard configuration or telemetry is fetched again.



On the first visit, when no saved state exists:

- Default to Compact presentation.
- Show all measurements (expanded).

## 3. Global Compact / Detailed behavior

Preserve the Milestone 5B semantics:

- Global Compact sets all widgets to Compact while preserving each widget's existing expanded/collapsed state.
- Global Detailed sets all widgets to Detailed and expands every widget.
- Both actions update the persisted state of all affected widgets.
- After a global action, individual widget controls remain independent.
- Repeated clicks on a global mode must reapply its behavior.

Do not reintroduce group-level collapsing.

## 4. Storage

Use browser `localStorage` for this display-only preference.



Requirements:

- Scope saved state by authenticated user, property ID and widget ID.
- Avoid collisions between different users and properties.
- Store no credentials, telemetry values or sensitive data.
- Handle missing, malformed or unavailable storage gracefully.
- Remove stale entries for widgets that no longer exist when safe.
- Do not overwrite existing saved choices during asynchronous dashboard loading.
- Keep storage writes limited to actual user-initiated state changes.
- Do not add database migrations or new API endpoints.

Note that browser-local preferences are not expected to synchronize across different browsers or devices.

## 5. Verification

Test:

- Different Compact/Detailed states across multiple widgets.
- Independent expanded/collapsed states.
- Refresh and browser reopening.
- Navigation away and back.
- Switching properties.
- Global Compact preserving collapsed widgets.
- Global Detailed expanding all widgets.
- Local override after a global action.
- Telemetry updates not affecting state.
- Missing or corrupt localStorage.
- Widget deletion and recreation.

Run relevant frontend tests and build.



Archive the prompt

## Execution summary

- [Dashboard widget preferences](../../src/frontend/home-automation-web/src/features/dashboardWidgetViews.ts) now store the mode and measurement expansion separately for each widget, property, and authenticated account in browser localStorage. New widgets start Compact with all measurements expanded.
- Global Compact preserves measurement expansion. Global Detailed expands every widget. Deleted widget preferences are dropped after a successful layout fetch and removed from localStorage on the next view change.
- The frontend build and all 47 frontend tests passed. A headless Chrome check with fixture data verified refresh, property switching, global and individual controls, and closing and reopening the browser.
