# HomeAutomation — Compact Widget Header Icons and Brighter Green

Date: 2026-11-24

## Original prompt

# HomeAutomation — Compact Widget Header Icons and Brighter Green

Make a small, focused frontend-only change to the configurable monitoring Dashboard.

1. Replace the per-widget Compact / Detailed segmented buttons with ONE small icon button positioned at the far right of the widget title, immediately beside the widget navigation button.
2. Use Lucide icons:
   - `rows-3` when Compact is active (click switches to Detailed).
   - `rows-2` when Detailed is active (click switches to Compact).
   - Replace the existing widget navigation icon with `arrow-up-right`.
3. Both icon buttons must have:
   - Matching icon size (16px).
   - Matching clickable area (approximately 30x30px).
   - Accessible aria-label and tooltip.
   - Clear hover and keyboard-focus states.
4. Keep the widget title on the left and both icons right-aligned:\
   Kitchen                                      [view icon] [navigation icon]
5. Preserve behavior:
   - Local widget toggle changes only that widget.
   - Global Compact / Detailed buttons override all local states.
   - Group chevrons and widget expansion remain independent.
   - Telemetry updates do not reset display state.
   - Clicking either icon never sends a device command.
   - Navigation continues to use the authorized internal route.
6. Change the Fresh measurement value color from `#007F36` to `#00A344`.
   - Apply only to fresh values.
   - Keep intermediate blue and stale gray unchanged.
   - Keep backgrounds and metadata unchanged.
   - Check/report contrast against the existing #F4F4F4 tile background; do not silently override the requested color.
7. Add focused frontend tests for icon switching, accessible labels, global override, and navigation. Verify desktop and mobile rendering.

Run the frontend build and relevant tests. Archive the prompt and commit locally without pushing.



Do not change backend APIs, database schema or provider integrations.

## Execution summary

Replaced the widget-level segmented control with a single 16px Lucide Rows3 or Rows2 icon beside the 16px ArrowUpRight link. The controls share 30px hit areas, hover and focus styles, and descriptive labels and tooltips. The existing local and global mode rules still apply.

Fresh values now use the requested `#00a344`. Against `#f4f4f4` it measures 3.02:1, below the 4.5:1 WCAG AA minimum for the dashboard's normal-size values. Intermediate and stale values use their existing colors. [WCAG 2.2 text contrast](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html)

The frontend build and all 42 tests passed. Headless Chrome verified matching icon sizes and hit areas, right alignment, tooltips, hover and keyboard focus, authorized navigation, and the requested green on a valid zero reading at 1440 and 390 pixels.

- [Widget icon controls](../../src/frontend/home-automation-web/src/features/DashboardWidgets.tsx)
- [Dashboard regression tests](../../src/frontend/home-automation-web/src/features/Dashboard.test.tsx)
