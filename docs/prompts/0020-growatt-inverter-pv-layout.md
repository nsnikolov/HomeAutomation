# Growatt UI — Side-by-Side Inverters and PV Monitoring

Date: 2026-11-23

## Original prompt

# Growatt UI — Side-by-Side Inverters and PV Monitoring

Improve the Growatt monitoring page.

## 1. Responsive layout

- Display Growatt Master and Growatt Slave side by side in two equal-width columns on desktop.
- Stack vertically on narrow screens.
- Preserve all existing telemetry, status indicators and drag-and-drop ordering.
- Reduce unnecessary whitespace.
- Keep the Solar Installation summary unchanged.

## 2. PV monitoring

For each inverter, prominently display:

- PV input voltage (V)
- PV input current (A), if available
- PV input power (W), if available

If the provider exposes multiple PV inputs or strings, show their measurements separately.



Reuse existing Growatt telemetry where possible. Inspect the existing provider mapping before adding fields.



Do not invent unavailable values or estimate PV power from voltage alone.



Distinguish between:

- PV voltage present
- PV actively generating power
- Stale or unavailable measurements

Keep all values read-only.

## 3. Verification

- No changes to physical inverter configuration.
- No additional Growatt API calls unless strictly necessary.
- Preserve existing polling and rate-limit handling.
- Add focused tests if telemetry mapping changes.
- Run frontend build and relevant tests.
- Commit locally. Do not push.

Keep the change small and avoid unrelated refactoring.

## Execution summary

Placed the inverter cards side by side on wide screens and stacked them on narrow screens. The shared battery uses a full-width row on desktop. The existing order, controls, and Solar Installation summary are retained. The PV display uses the existing reported voltage and power, distinguishes generation from voltage presence and stale data, and groups PV-bearing endpoints separately when supplied. PV current stays unavailable with the current mapping.

The frontend production build and all 20 frontend tests passed. A local Chrome fixture verified the layout at 1,160 px and 600 px. The changes made no Growatt API requests and left polling and inverter settings in place.

- [Growatt device cards](../../src/frontend/home-automation-web/src/features/GrowattPanel.tsx)
- [Frontend monitoring tests](../../src/frontend/home-automation-web/src/features/Monitoring.test.tsx)
