# Milestone 4C — eWeLink Capability-Based UI and Live Telemetry Fix

Date: 2026-11-23

## Original prompt

# Milestone 4C — eWeLink Capability-Based UI and Live Telemetry Fix

Continue the existing HomeAutomation repository from the latest committed main branch.



Context:

- eWeLink OAuth and cloud discovery are working with the real account.
- Five PSF-X67 relays and one THR320D controller are discovered.
- The official eWeLink app shows seven devices, including "Thermostat Bidon", which remains missing.
- All device cards currently show Temperature, Humidity, Active Power, Voltage, Current, Cumulative Energy and Energy Today, even when unsupported.
- The THR320D reports temperature and humidity in eWeLink, but HomeAutomation displays Unavailable.
- InventoryDiagnostics was introduced in Milestone 4B.

## Goal

Display only capabilities genuinely supported by each device, and fix the mapping of real eWeLink sensor telemetry.

## 1. Capability-based device cards

- Display Relay status only for devices exposing a relay.
- Display Temperature only when the device supports temperature measurement.
- Display Relative Humidity only when supported.
- Display electrical power (W), voltage (V), current (A), and energy (kWh) only when supported by verified provider metadata or datapoint fields.
- Do not render unsupported measurement rows with "Unavailable".
- For a supported capability with a temporarily missing or stale reading, preserve the row and show an appropriate unavailable/stale state.
- Keep online/offline state and cloud synchronization information visible.
- Do not use generic placeholder capabilities on every device.

## 2. Fix THR320D temperature and humidity

The installed "THR pipe heater" (THR320D) displays approximately 12.9°C and 100% relative humidity in the official eWeLink app.



Investigate:

- Existing CoolKit thing response parsing.
- THR320D UIID and documented parameter names.
- Whether readings are returned in thing inventory, device parameters, or another authorized read endpoint.
- Units, scaling, timestamps and freshness.

Map actual temperature and humidity when verified.



Do not guess sensor values or scaling.



If the existing cloud response lacks these readings, explain precisely what is missing and whether an additional API request is necessary.

## 3. Electrical measurement support

Check the PSF-X67 devices and other discovered devices for actual power-monitoring datapoints.

- Display active power only if a real measurement exists.
- Display daily energy only if an official daily counter or verified historical data supports it.
- Do not infer consumption from relay state.
- Do not invent watts, voltage, current or energy.
- Do not increase polling frequency unnecessarily.

## 4. Missing Thermostat Bidon

Review the existing eWeLink discovery diagnostics.



Determine whether the missing device:

- Is omitted by CoolKit Cloud API;
- Has an unsupported UIID or brand entitlement;
- Is filtered by current discovery or mapping;
- Belongs to a family not being enumerated.

Fix local filtering issues if identified.



Do not invent the device if it is absent from provider results.

## 5. Safe diagnostics

Use the existing opt-in Ewelink:InventoryDiagnostics capability.



When enabled, log sanitized:

- Device model/UIID
- Available parameter names and JSON types
- Supported capabilities
- Discovery counts and filtering reasons

Never log secrets, authorization codes, tokens, device IDs, account identifiers or full raw provider payloads.



Reuse existing provider requests and respect the API quota.

## 6. Tests and delivery

Add focused regression tests for:

- Relay-only devices showing only relay state.
- THR320D temperature and humidity mapping.
- Supported but missing measurements.
- Unsupported measurement row hiding.
- Power-monitoring capability detection.
- Missing device discovery scenarios.

Run backend and frontend builds and relevant tests.



Do not change OAuth, token encryption or property authorization.\
Do not add physical device commands.\
Do not alter existing autonomous thermostat rules.



Update eWeLink documentation and archive this prompt.



Commit verified changes locally. Do not push.



Report:

- Confirmed capabilities by device type.
- Which real measurements now work.
- Any remaining provider limitations.
- Test results and commit hash.

## Execution summary

The eWeLink cards now use verified relay and measurement capabilities. Supported rows distinguish missing readings from last-known values. Exact THR320D and THR316D model metadata can select the thermostat parser when UIID is absent, and recognized probe metadata keeps supported rows visible without generating measurements.

A read-only query of existing poll snapshots confirmed 12.9 °C and 100% relative humidity for THR320D and active power, voltage, and current for all five PSF-X67 devices. The partial inventory still contains six devices. The stored data cannot determine whether CoolKit omitted Thermostat Bidon or returned a row that the adapter could not use. Opt-in diagnostics now report model allowlist labels, parameter types, supported capabilities, home coverage and filtering reasons. No extra provider requests or device commands were made.

Both production builds passed. Backend tests recorded 116 passes and four opt-in MySQL skips. All 23 frontend tests passed. See the [eWeLink guide](../integrations/ewelink-cloud.md#discovery-and-capability-based-measurements) and [device cards](../../src/frontend/home-automation-web/src/features/EwelinkPanel.tsx).
