# Dashboard — Fix Freshness Thresholds and Add Compact/Detailed Toggle

Date: 2026-11-24

## Original prompt

# Dashboard — Fix Freshness Thresholds and Add Compact/Detailed Toggle

The configurable monitoring dashboard works, but needs two UX corrections.

## 1. Fix measurement freshness colors

Actual observed dashboard:

- Daikin Kitchen temperature: green, under 5 minutes old — correct.
- Daikin target temperature: green, under 5 minutes old — correct.
- Tuya Kitchen thermometer: blue, although the displayed timestamp is more than 60 minutes old — incorrect.

Expected behavior, calculated independently for every measurement:

- Green: age < 5 minutes.
- Blue: 5 minutes <= age < 60 minutes.
- Gray: age >= 60 minutes.
- Unavailable: missing/invalid timestamp or reading.

Verify the actual observation timestamps, browser time and timezone conversions. Use absolute UTC instants for age calculations, not formatted local date strings.



Ensure:

- Exact 5-minute boundary is blue.
- Exact 60-minute boundary is gray.
- Colors update over time without requiring telemetry updates.
- Numeric zero is valid.
- Tooltip/details show the actual timestamp and source provenance.
- Both Compact and Detailed modes use the same freshness logic.
- Add focused regression tests for the 5/60-minute boundaries and rendered color.

## 2. Add Dashboard Compact / Detailed toggle

Add a clearly visible toggle in the main Dashboard header:



[Compact] [Detailed]



Requirements:

- It must work in monitoring mode, without opening Configure dashboard.
- Applies to all widgets in the current property dashboard.
- Compact shows only measurement labels, values and freshness colors.
- Detailed additionally shows provider, source device, exact timestamp and timestamp provenance.
- Switching modes must not reload the page or issue new provider API requests.
- Existing widget-level displayMode remains the default when no global override is selected.
- Make the global override temporary or persist it per user/property in client-side storage; do not modify the shared dashboard configuration.
- Preserve independent group/widget collapse state when switching modes.
- Ensure responsiveness on desktop and mobile.
- Preserve read-only widget navigation and accessibility.

## 3. Verification

- Test switching views with real Daikin and Tuya measurements.
- Confirm Tuya readings older than 60 minutes render gray.
- Verify green/blue/gray boundaries.
- Run relevant frontend tests and build.
- Avoid unrelated backend, provider or database changes.

Archive the prompt and commit locally without pushing.

## Execution summary

Audited the three saved dashboard readings from local MySQL. At 18:02 Sofia time, Daikin room and target temperatures were about three minutes old with server-received provenance. Tuya temperature was about 76 minutes old with provider provenance. Headless Chrome's UTC clock matched the system clock. Replaying those observations reproduced the Tuya `old` class with the previous blue-tinted slate color, so the visible discrepancy came from the palette.

The old tier now uses neutral gray. Age and timers use the same validated observation instants, and tooltips include the original offset-bearing timestamp and provenance. The temporary header toggle applies Compact or Detailed to the current dashboard. Selecting the active mode again restores saved widget modes. Expansion state and widget navigation use the existing components.

The frontend build and all 40 tests passed. Chrome replay of the actual saved values at desktop and mobile widths with Sofia and New York timezone settings confirmed neutral gray for the old Tuya reading, detailed provenance, and no additional fetches from switching modes. The read-only audit made no changes to local MySQL.

- [Dashboard rendering and freshness](../../src/frontend/home-automation-web/src/features/DashboardWidgets.tsx)
- [View toggle usage](../../README.md#configurable-monitoring-dashboard)
