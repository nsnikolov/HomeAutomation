# Growatt — Display PV1 Voltage

Date: 2026-11-23

## Original prompt

# Growatt — Display PV1 Voltage

On the Growatt monitoring page, display the actual PV1 input voltage (V) separately for each inverter (Master and Slave).



Requirements:

- Inspect the existing Growatt API response and telemetry mapping.
- Identify the field corresponding to ShinePhone's "PV1 Voltage".
- Map and display that value prominently in each inverter's PV input section.
- Reuse existing API calls and polling.
- Keep the existing PV power display.
- Do not substitute total PV voltage for PV1 unless the provider explicitly defines them as equivalent.
- If PV1 voltage is unavailable, display "Unavailable" rather than 0 V.
- Preserve the existing layout and functionality.
- Add focused tests and run the relevant builds.
- Commit locally. Do not push.

Keep the change minimal. No physical inverter commands.

## Execution summary

Mapped the optional Growatt `vpv1` field to a distinct, volt-valued telemetry metric for each inverter in the existing batch poll. Added a PV1 voltage row to the inverter cards and retained general PV voltage, PV power, and freshness indicators. Missing or invalid PV1 values do not generate readings. Existing telemetry storage requires no migration.

The .NET Release and frontend production builds passed. Backend tests recorded 104 passes and three opt-in MySQL skips. All 20 frontend tests passed. Fixtures cover numeric and numeric-string PV1 values for the two inverters and missing, null, and invalid values. Live SPF field availability was not verified. No additional Growatt API requests or device commands were made.

Relevant files include [Growatt cloud mapping](../../src/backend/HomeAutomation.Infrastructure/Growatt/GrowattCloudSource.cs), [Growatt panel](../../src/frontend/home-automation-web/src/features/GrowattPanel.tsx), and [integration documentation](../integrations/growatt.md).
