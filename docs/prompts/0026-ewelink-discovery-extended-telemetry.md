# Milestone 4B — eWeLink Discovery and Extended Telemetry

Date: 2026-11-23

## Original prompt

# Milestone 4B — eWeLink Discovery and Extended Telemetry

Continue the existing HomeAutomation repository from the latest committed main branch (eWeLink OAuth SQL hotfix eb528a8).



The eWeLink OAuth connection now works with the real account. Six devices are discovered, but the official eWeLink application displays seven.

## Goals

1. Discover the missing device.
2. Show temperature and humidity for supported devices.
3. Show instantaneous power and daily energy consumption where supported.

## 1. Missing device

The official eWeLink app shows seven devices:

- Boiler — PSF-X67
- Cooker — PSF-X67
- Inverter In — PSF-X67
- Inverter out — PSF-X67
- Heatpump — PSF-X67
- THR pipe heater — THR320D
- Thermostat Bidon — temperature controller

HomeAutomation currently discovers the first six. Thermostat Bidon is missing.



Investigate the existing family/thing discovery logic:

- Family enumeration
- Pagination and filters
- Different device types and model capabilities
- Shared or unsupported devices
- Developer API brand/entitlement restrictions
- Device deduplication and persistence

Do not invent missing devices. If the provider does not return Thermostat Bidon, document the reason or remaining uncertainty.

## 2. Temperature and humidity

THR pipe heater (THR320D) currently shows Unavailable for both measurements.



The official app displays approximately:

- Temperature: 12.9°C
- Humidity: 100%

Identify the correct device-specific parameters, units and scales from the official CoolKit API and actual available data.



Add temperature and humidity capabilities only where verified.



Preserve the existing relay status and autonomous thermostat rules.

## 3. Electrical measurements

Investigate whether the PSF-X67 devices and other discovered devices expose:

- Instantaneous active power (W)
- Voltage (V)
- Current (A)
- Energy consumed today (kWh)
- Cumulative energy (kWh)

Display supported values on the corresponding device cards.



Important:

- Relay ON/OFF status is not evidence of power consumption.
- Do not estimate power from relay state.
- Do not confuse W, VA, Wh and kWh.
- Do not fabricate daily energy from sparse snapshots.
- Only derive daily energy from verified cumulative counters with correct rollover/reset and local-day handling, or use an official daily counter.
- If unavailable through the API, display Unavailable.

## 4. API inspection

Inspect existing API response mapping and official CoolKit device protocol documentation.



Reuse existing polling and authenticated requests.



Do not make extra live API requests without explicit operator approval.



If provider-specific fields remain uncertain, add safe, opt-in diagnostics that report field names, JSON types and supported capabilities without logging device IDs, secrets, tokens or full payloads.



Respect API quotas, timestamps and freshness.

## 5. Safety

This milestone is read-only.

- Do not add relay control commands.
- Do not modify thermostat setpoints or automation rules.
- Do not change OAuth security or token persistence.
- Preserve existing property authorization and ordering.
- No changes to Growatt, Daikin or Tuya behavior.

## 6. Tests and delivery

Add focused tests for:

- Seven-device discovery and missing-device scenarios
- Temperature and humidity parsing and scaling
- Power measurement parsing
- Daily energy counters when supported
- Missing fields and stale data
- Property isolation and duplicate prevention

Run relevant builds and tests.



Update eWeLink documentation and archive the prompt.



Commit locally. Do not push.



Report which capabilities are confirmed, which remain unavailable, and whether further live API inspection is needed.

## Execution summary

A read-only MySQL inventory check confirmed five PSF-X67 devices and one THR320D. The stored snapshots do not retain raw provider parameters or original discovery counts. Discovery now includes a separately reported current home when missing from the homes list, retains owned/shared deduplication and marks incomplete results partial. Tests cover a seventh device supplied by the provider and a partial response that omits it. Thermostat Bidon's absence remains unverified at the provider.

The device cards display verified protocol readings when supplied in an online snapshot. Thermostat profiles accept decimal Celsius and relative humidity with a supported probe. Meter profiles accept decimal watts, volts and amps. The normalized snapshot preserves last-known values without generating numeric history from undated data. Daily and cumulative energy are unavailable without a verified passive source. Optional inventory diagnostics record sanitized counts, known field names and types, and capabilities on the normal poll. No extra live API requests or device commands were made.

Both production builds passed. Backend tests recorded 113 passes and four opt-in MySQL skips. All 21 frontend tests passed. See the [eWeLink guide](../integrations/ewelink-cloud.md#milestone-4b-discovery-and-measurements), [device mapping](../../src/backend/HomeAutomation.Infrastructure/Ewelink/EwelinkMapping.cs) and [device cards](../../src/frontend/home-automation-web/src/features/EwelinkPanel.tsx).
