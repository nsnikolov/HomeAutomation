# Growatt — Power Flow Monitoring and Available Telemetry Audit

Date: 2026-11-23

## Original prompt

# Growatt — Power Flow Monitoring and Available Telemetry Audit

Continue the existing HomeAutomation project.



Current Growatt UI supports two parallel SPF 5000 ES inverters (Master and Slave), PV1 voltage, PV power, AC output readings, and shared battery monitoring.



Latest Growatt commit: `efb78d4`.

## Goal

Expose accurate load consumption and grid import power for each inverter, and identify other useful telemetry available through the existing Growatt API.

## 1. Audit available telemetry

Inspect:

- Existing Growatt V4 API client and polling responses.
- Current DTOs and telemetry mapping.
- Existing persisted measurements.
- Official Growatt API documentation and model-specific field semantics.

Determine which fields are actually returned for the two installed SPF 5000 ES units.



Prioritize:

- AC output/load active power (W) per inverter
- AC input/grid import active power (W) per inverter
- Battery charging/discharging power (W)
- PV1 voltage and PV power
- AC input/output current (A), if available
- Battery voltage, current and SOC
- Inverter temperature and warnings
- Daily energy counters (kWh), if available

Distinguish real active power (W), apparent power (VA), current (A), and load percentage (%).



Do not invent values or assume that similarly named fields have identical meanings across Growatt models.

## 2. Power flow UI

For each inverter, prominently show:

- PV input power (W)
- Load/output power (W)
- Grid import power (W)
- Battery charging or discharging power (W), if available

Retain the existing PV1 voltage and all current telemetry.



Do not duplicate metrics if "Inverter output" already represents load/output power; clarify or rename its label instead.



If a field is unavailable, display "Unavailable".



Do not derive grid import from AC voltage, load percentage, or unrelated power measurements.



Only calculate derived values if the energy-flow model, units, direction, and timestamps are explicitly verified.

## 3. Parallel inverter behavior

These two inverters operate in parallel.

- Preserve individual measurements for Master and Slave.
- Add installation totals only when source readings are compatible and sufficiently synchronized.
- Avoid counting the shared battery twice.
- Avoid treating grid bypass, inverter output, and PV generation as interchangeable.
- Clearly distinguish power supplied to loads from the inverter's own internal consumption.

## 4. Diagnostics and discovery

Produce a sanitized inventory of available Growatt telemetry fields, including:

- API field name
- Description
- Unit and scale
- Supported/unsupported status
- Whether the field appears in actual device responses
- Whether it is already mapped or displayed

Do not log credentials, tokens, serial numbers, or complete raw responses.



If live provider inspection would require additional API calls, do not perform them automatically. First inspect existing stored samples and existing polling data.

## 5. Implementation and tests

- Reuse existing Growatt polling.
- Preserve current rate limits and retry behavior.
- Keep all functionality read-only.
- No inverter configuration changes.
- No database migration unless strictly necessary.
- Add focused backend mapping and frontend tests.
- Run relevant builds and tests.
- Update Growatt integration documentation.
- Archive this prompt.
- Commit locally; do not push.

Keep implementation focused.



At completion, report which requested measurements are genuinely available, which are not, and any uncertainty about SPF 5000 ES field semantics.

## Execution summary

A read-only MySQL audit confirmed per-inverter AC output active power, AC charging power, PV power, general PV voltage, AC input voltage and frequency, AC output voltage, load percentage, warnings, and operating mode. Battery voltage and SOC are stored once from the configured Master source. PV1, grid import, and signed battery power had no stored series. The stored measurements and existing poll logs cannot verify fields the adapter discards. No additional Growatt requests were sent.

The inverter cards present AC load/output, grid import, and battery charge/discharge alongside existing PV measurements. The existing output reading is shown once. Unknown flows display Unavailable, and AC charging retains its own label. No new aggregation, ingestion behavior, or migration was introduced.

The .NET Release and frontend production builds passed. Backend tests recorded 104 passes and three opt-in MySQL skips. All 20 frontend tests passed. See the [sanitized telemetry inventory](../integrations/growatt.md#spf-5000-es-telemetry-inventory), [Growatt panel](../../src/frontend/home-automation-web/src/features/GrowattPanel.tsx), and [mapping tests](../../tests/HomeAutomation.UnitTests/GrowattCloudTests.cs).
