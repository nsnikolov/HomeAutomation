# Growatt SPF 5000 ES — Fix PV Display and Missing Power Metrics

Date: 2026-11-23

## Original prompt

# Growatt SPF 5000 ES — Fix PV Display and Missing Power Metrics

Improve the Growatt page for the two parallel SPF 5000 ES inverters (master and slave).

## Confirmed behavior

- The generic "Voltage" field already shows the correct PV voltage.
- During daylight, its value is correct.
- At night, it correctly shows 0 V.
- "PV1 voltage" incorrectly shows Unavailable.
- "Current" shows Unavailable.
- "PV input power" already works correctly.
- "Grid import power" and "Battery charge/discharge" show Unavailable, although the official Growatt application displays these measurements.

## Task 1 — PV display correction

1. Reuse the existing generic "Voltage" value under the "PV1 voltage" label.
2. Remove the redundant generic "Voltage" field.
3. Preserve 0 V as a valid measurement, especially at night. Never treat zero as missing data.
4. Remove the "Current" field if the existing provider does not expose a valid PV current value.
5. Keep "PV input power" unchanged.
6. Apply these changes consistently to master and slave.
7. Avoid unnecessary backend changes if the correction can be handled in the frontend.

Expected PV section:

- PV1 voltage: 0 V (at night) or actual voltage during daylight
- PV input power: existing measured value

## Task 2 — Grid import power

Inspect the actual Growatt Cloud API responses and existing integration code for SPF 5000 ES.



Identify whether the provider exposes grid import power directly.

- Check unused response fields and relevant storage-inverter endpoints.
- Verify field semantics, units and scaling.
- Distinguish grid import from AC charging power and AC input voltage.
- Map the correct measurement if available.
- Do not calculate grid power from voltage alone.
- Avoid double counting between master and slave.

If the measurement is unavailable, document the precise limitation.

## Task 3 — Battery charging/discharging

Inspect actual available Growatt fields, including pCharge, pDischarge, pBat and model-specific equivalents.



If supported:

- Map charging and discharging power correctly.
- Preserve zero as a valid measurement.
- Display direction explicitly: Charging or Discharging.
- Verify sign conventions and units.
- Avoid double counting the shared battery in the parallel inverter configuration.
- Do not estimate battery power from SOC changes.

## Task 4 — Tests and diagnostics

- Add sanitized, opt-in diagnostics for available provider fields and mapping decisions.
- Do not log credentials, serial numbers or sensitive payloads.
- Add backend/frontend tests covering nighttime 0 V, daylight voltage, missing data, charging/discharging, and parallel inverters.
- Preserve existing Growatt polling intervals, API quotas, backoff, and error handling.
- Do not send inverter control commands.
- Run builds and tests.
- Archive this prompt and commit locally without pushing.

## Final report

Explain:

1. What changed in the PV UI.
2. Which source field provides grid import power, if found.
3. Which fields provide battery charging/discharging, if found.
4. Which measurements remain unavailable and why.

## Execution summary

Consolidated the PV voltage into one PV1 voltage row per inverter, using the verified generic `vpv` as a fallback. Nighttime zero is displayed and PV input power continues to use `ppv`. Removed the PV input current placeholder because `ipv` remained zero during generation and `iChargePV1` describes charging current.

Read-only V4 history checks confirmed `pAcInPut` and signed `pBat` for both configured inverters. The adapter maps these fields when present in the existing latest response. Negative vendor battery power corresponds to charging and positive to discharging. The dashboard presents these per inverter and leaves shared battery net flow and utility-side import unavailable. The V1 storage endpoint rejected the account with code `10011`. No site totals are derived and no inverter commands were sent. Opt-in diagnostics expose only field presence, kinds and mapping decisions on normal polls.

Both builds passed. Backend tests passed 158 with seven opt-in database tests skipped. Frontend tests passed 34. No migration was needed.

- [Field audit and diagnostics](../integrations/growatt.md#spf-5000-es-telemetry-inventory)
- [Growatt cloud mapping](../../src/backend/HomeAutomation.Infrastructure/Growatt/GrowattCloudSource.cs)
