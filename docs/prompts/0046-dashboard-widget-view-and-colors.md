# HomeAutomation — Improve Freshness Colors and Add Widget-Level View Toggle

Date: 2026-11-24

## Original prompt

# HomeAutomation — Improve Freshness Colors and Add Widget-Level View Toggle

Improve the existing configurable monitoring Dashboard presentation.

## 1. Freshness colors

The current green text is too dark and visually resembles stale gray.



Use the following proposed light-theme colors:

- Fresh (< 5 minutes): #**00A344**— brighter green
- Intermediate (5 to < 60 minutes): #087CE8 — clear blue
- Stale (>= 60 minutes): #666666 — neutral gray

Requirements:

- Check actual WCAG contrast against the dashboard measurement backgrounds.
- Adjust shades slightly if necessary to satisfy accessibility.
- Keep freshness classification and timestamp logic unchanged.
- Apply colors to measurement values only, not all widget text.
- Preserve valid zero values.
- Support existing dark theme if applicable.
- Verify actual computed colors in Chrome.

## 2. Individual widget Compact / Detailed toggle

Currently the Dashboard has global Compact/Detailed buttons, and groups have independent expand/collapse chevrons.



Add a small Compact/Detailed toggle to EACH widget header, aligned to the far right of its title, next to the existing navigation/link control.



Example:



Kitchen                                   [Compact | Detailed] [↗]



Behavior:

- Compact: display measurement label, value and freshness color without verbose metadata.
- Detailed: additionally show provider, device name, timestamp and provenance.
- Switching one widget must not affect other widgets or groups.
- Clicking the toggle must never trigger widget navigation or device commands.
- Use accessible buttons, clear active state and keyboard support.
- Preserve the selected local mode across telemetry updates.
- Avoid database writes for display-only changes.

## 3. Global and group behavior

Preserve the existing global Compact/Detailed controls and group-level expand/collapse chevrons.

- Global Compact forces all widgets into Compact and collapses all groups and widgets.
- Global Detailed forces all widgets into Detailed and expands all groups and widgets.
- A global action overrides previous individual widget modes and expansion states.
- After a global action, each widget may independently switch Compact/Detailed.
- Group chevrons continue to expand/collapse only their own group.
- Repeated global button clicks reapply the selected mode.
- No unexpected restoration of older local states.

Keep the group expansion state independent of individual widget presentation wherever possible.

## 4. Verification

- Test three widgets with different local modes.
- Test global Compact and Detailed overriding mixed local settings.
- Test local widget changes after a global action.
- Verify that changing one widget does not affect others.
- Verify rendered freshness colors and contrast in Chrome.
- Check desktop, tablet and mobile layouts.
- Preserve widget navigation and read-only monitoring.
- Run relevant frontend tests and build.
- Archive the prompt and commit locally without pushing.

Do not modify provider integrations, database schema, polling or device-control behavior.

## Execution summary

The measurement tiles use `#f4f4f4`. The proposed green and blue measured 3.02:1 and 3.77:1 against that background, so the dashboard uses `#007f36` and `#006aca`. The proposed `#666666` remains the stale color. The three ratios are 4.67:1, 4.88:1, and 5.22:1 against the actual tile background. The app has a light dashboard palette. [WCAG 2.2 text contrast](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html)

Added native Compact and Detailed buttons in each monitoring widget header beside its navigation link. Widget display choices carry the current global action generation, so they survive incoming observations and expire when a global action is reapplied. The local controls preserve widget and group expansion. Freshness thresholds and original timestamps remain the source of the displayed freshness tier.

The frontend production build and all 42 tests passed. Chrome verified the rendered values and contrast in both modes at 1440, 820, and 390 pixels. The isolated fixture also confirmed keyboard activation, a preserved navigation link, a valid zero reading, and no provider or write requests while switching modes.

- [Widget mode controls](../../src/frontend/home-automation-web/src/features/DashboardWidgets.tsx)
- [Dashboard regression tests](../../src/frontend/home-automation-web/src/features/Dashboard.test.tsx)
