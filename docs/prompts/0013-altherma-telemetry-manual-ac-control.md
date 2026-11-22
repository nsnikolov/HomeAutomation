# Milestone 3D — Daikin Altherma Telemetry and Manual AC Control

Date: 2026-11-22

## Original prompt

# Milestone 3D — Daikin Altherma Telemetry and Manual AC Control

Act as a Senior .NET 10 Engineer, IoT Integration Specialist, and React Engineer.

Extend the existing Home Automation Daikin ONECTA integration with Altherma-specific telemetry and enable safe, manually initiated control of supported air conditioners.

Read AGENTS.md, relevant Codex Skills, architecture documentation, and existing Daikin integration code before making changes.

Preserve the existing application architecture and all working integrations.

## 1. Current State

The application already has:

- Working Daikin ONECTA OAuth authentication.
- Live device discovery.
- Property and room assignments.
- Background monitoring.
- Device capability discovery.
- Manual command infrastructure.
- Command validation and audit trail.
- Physical device writes disabled by default.
- Working Growatt integration.
- MySQL telemetry persistence.

The Daikin Altherma capability inventory is available at:

`docs/integrations/daikin-altherma-capabilities.md`

Review this document before implementation.

Preserve the existing authenticated account, encryption key, tokens and device assignments.

Do not reconnect the ONECTA account unnecessarily.

## 2. Altherma Telemetry

The existing device named `Heatpump` has been identified as a Daikin Altherma.

A previous real ONECTA API inspection confirmed:

- Leaving water temperature: 40°C.
- Target heating water temperature: 39°C.
- Advertised target range: 28–60°C.
- Target increment: 1°C.
- Heating control mode.
- Setpoint mode.
- Warning and emergency indicators.
- Three heating electrical-energy arrays.

These values represent one historical API observation, not current readings.

Use the exact provider field paths documented in the capability inventory.

Do not hardcode the example temperatures.

## 3. Implement Altherma-Specific Capabilities

Extend the provider-neutral capability model where necessary.

Add support for:

- Current leaving-water temperature.
- Target leaving-water temperature.
- Heating control mode.
- Water temperature setpoint mode.
- Warning and emergency indicators.

Preserve units, timestamps, data types and provider capability metadata.

Persist confirmed numeric telemetry in MySQL using the existing storage infrastructure.

Ensure previous readings remain available for historical queries.

Do not duplicate logical devices or battery/energy measurements.

Avoid introducing Altherma-specific fields into unrelated air conditioner capabilities.

## 4. Altherma Dashboard

Provide an appropriate heat-pump presentation.

Display:

- Current leaving-water temperature.
- Target leaving-water temperature.
- Outdoor temperature.
- Heating mode.
- Power state.
- Warning/emergency status.
- Last synchronization.
- Data freshness.

Replace irrelevant AC-centric labels such as "Indoor temperature: Unavailable" with the appropriate heat-pump measurements.

Show temperature history where sufficient persisted data exists.

Reuse existing charting and dashboard components where possible.

Keep the interface compact and clear.

The Altherma controls must remain read-only.

Do not enable water-temperature changes or power commands for Altherma.

## 5. Energy Dataset Investigation

The capability inventory identified three heating electrical-energy arrays.

Review their structure and metadata.

Determine whether the provider exposes:

- Energy units.
- Sampling periods.
- Interval boundaries.
- Cumulative or interval values.
- Dataset category.

Only implement energy telemetry if units and interval semantics can be established reliably.

Otherwise document the remaining uncertainty.

Do not calculate COP from electrical energy alone.

Do not invent thermal output measurements.

## 6. Manual Air Conditioner Control

The existing application already implements a gated ONECTA command pipeline.

Review and reuse the existing command handlers rather than building a new control architecture.

Enable user-facing controls for supported air conditioners:

- Power On/Off.
- Target temperature adjustment.
- Heating/Cooling mode selection where supported.
- Fan speed adjustment where supported.

For this milestone, prioritize power and temperature.

Only expose controls when the relevant capability is advertised as writable.

Validate provider-specific allowed ranges and increments.

Never assume every ONECTA device supports identical commands.

## 7. Physical Write Safety

Physical control of real devices must remain disabled by default.

First verify all command paths using mocked ONECTA responses.

Provide an explicit configuration flag to enable real writes only after user authorization.

The flag must not bypass:

- Device capability validation.
- Property authorization.
- Fresh telemetry checks.
- Device online checks.
- Command auditing.
- Duplicate command prevention.
- Conflict prevention.
- Verification of observed device state.

Do not enable real writes automatically as part of startup or deployment.

Do not send physical commands during automated tests.

For a supervised live test, require explicit user approval before enabling the flag and executing any real command.

Never send commands to the Altherma as part of this milestone.

## 8. Frontend Controls

Improve the existing Daikin ONECTA device cards.

For supported air conditioners:

- Display current and target temperatures.
- Provide a Power On/Off control.
- Provide temperature + / - controls.
- Display operating mode.
- Show whether commands are available.
- Indicate command progress and result.
- Show meaningful errors if a command is rejected.

If writes are disabled, explain this clearly in the UI.

Do not present a disabled command as if it succeeded.

Do not display a new setpoint as confirmed until the provider reports the updated state.

For Altherma, show monitoring only.

## 9. Polling and API Quotas

Preserve the existing ONECTA API quota management.

Respect the published 200-call rolling daily quota.

Reserve capacity for user-initiated commands and confirmation reads.

Avoid unnecessary discovery and polling requests.

Do not increase the polling frequency without a quota calculation.

## 10. Tests and Verification

Add or update tests covering:

- Altherma capability mapping.
- Water-temperature unit conversion.
- Missing and stale measurements.
- Historical telemetry persistence.
- Air conditioner power commands.
- Temperature increment and range validation.
- Unsupported command rejection.
- Property authorization.
- Read-only mode.
- Pending/uncertain command handling.
- Correct provider state confirmation.
- Separation of Altherma and AC capabilities.

Run:

- .NET Release build.
- Backend tests.
- Frontend production build.
- Frontend tests.

Perform read-only live verification using the existing ONECTA connection where appropriate.

Do not execute physical commands without explicit user approval.

## 11. Documentation

Update:

- `docs/integrations/daikin-onecta.md`
- `docs/integrations/daikin-altherma-capabilities.md`
- Relevant architecture documentation if required.

Save the complete prompt using the next sequential number under `docs/prompts/`.

Update the prompt index.

Document which controls are implemented, which are enabled, and which require a supervised test.

## 12. Final Report

Provide:

1. Implemented Altherma telemetry.
2. Verified real measurement values.
3. UI improvements.
4. Implemented air conditioner controls.
5. Physical write status.
6. API quota impact.
7. Backend and frontend test results.
8. Any unavailable measurements.
9. Exact steps for enabling supervised manual control.
10. Remaining work.

Commit verified code changes locally with a descriptive message.

Do not push automatically.

Prioritize reliable monitoring and safe manual control over additional features.

## Execution summary

Implemented read-only Altherma monitoring and water-temperature history in the existing Daikin adapter, MySQL writer, property API, and React device card. Kept manual AC power, temperature, heating/cooling, and fixed fan speed gated by provider capabilities, property access, a fresh device timestamp, quota, and audited readback. Live writes remained disabled. ONECTA reported heating electricity in kWh, and the unit's bucket boundaries and accumulation semantics remain unverified.

Release build and frontend production build succeeded. All 50 backend tests and 9 frontend tests passed. Live read-only verification confirmed 40 °C leaving water and a 39 °C target. The scheduled worker persisted both series under the existing binding. The production history query returned one point per series and isolated the property. Loopback liveness and readiness returned 200, HTTPS served the app and proxied readiness, and the anonymous Daikin route returned 401.

- [ONECTA integration guide](../integrations/daikin-onecta.md)
- [Altherma capability inventory](../integrations/daikin-altherma-capabilities.md)
- [Daikin adapter](../../src/backend/HomeAutomation.Infrastructure/Daikin/)
- [Daikin device card](../../src/frontend/home-automation-web/src/features/DaikinPanel.tsx)
