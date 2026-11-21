# Milestone 2C — Activate Live Growatt Monitoring

Date: 2026-11-21

## Original prompt

# Milestone 2C — Activate Live Growatt Monitoring

Act as a Senior .NET Engineer and IoT Integration Specialist.

Extend the existing Home Automation application by activating and validating live Growatt monitoring through the already implemented Growatt Cloud V4 adapter.

Review AGENTS.md, relevant Codex Skills, architecture documents, Growatt integration documentation, and the existing implementation before making changes.

## 1. Confirmed Hardware Configuration

The installation contains two Growatt SPF 5000 ES inverters operating in parallel:

- Master: `[REDACTED]`
- Slave: `[REDACTED]`
- One shared LiFePO4 battery, 51.2 V / 200 Ah
- BMS communication
- Approximately 4 kWp of solar panels
- Grid connection and external ATS

Both inverters are currently operational.

Previously, the Slave remained offline following a Low Battery shutdown and required a manual power cycle to recover.

This behavior is relevant for monitoring and alerts but must not trigger automated inverter control.

## 2. Existing API Access

Growatt Cloud V4 authentication and read-only discovery have already been verified.

The API token is stored locally in .NET User Secrets under:

`Growatt:ApiToken`

Reuse the existing authenticated V4 adapter.

Do not request the token again.

Never print, log, expose, or commit credentials.

## 3. Configure Physical Devices

Map the confirmed serial numbers to the correct device identities.

- Master and Slave must remain separate physical devices.
- Both belong to one logical solar installation.
- Both share one logical battery.
- Prefer Master as the initial candidate for battery SOC and voltage readings.
- Verify this mapping using actual available telemetry before making it authoritative.

Do not create duplicate logical batteries or duplicate installation measurements.

## 4. Validate Live Telemetry

Perform limited read-only API requests.

Verify available measurements from both inverters:

- PV voltage and power
- AC input voltage and frequency
- AC output voltage
- Grid status
- Operating mode
- Battery SOC
- Battery voltage
- Battery charging/discharging power
- Inverter load
- Warnings and faults
- Last data timestamp
- Online/offline status

Do not invent unsupported fields or derive grid status from stale data.

Preserve source timestamps independently from ingestion timestamps.

Clearly identify any unknown or unsupported measurements.

Check which values are individual inverter readings and which represent totals for the entire installation.

Avoid double-counting household consumption, shared battery energy, and mirrored measurements.

## 5. Enable Continuous Polling

Once device identities and the required telemetry mappings are validated:

- Enable live Growatt monitoring.
- Use a polling interval of at least 300 seconds.
- Respect documented API quotas and retry instructions.
- Prevent overlapping requests.
- Handle temporary cloud outages.
- Persist readings in MySQL using the existing telemetry infrastructure.
- Mark stale or unavailable readings correctly.
- Ensure polling continues without an open browser.
- Keep the simulator running independently.

If validation fails, keep polling disabled and report the blocker.

## 6. Dashboard Integration

Update the existing React dashboard to display the real installation.

Provide:

- Master status
- Slave status
- Battery SOC and voltage
- PV production
- Available grid readings
- Charging/discharging state
- Available inverter load readings
- Latest warnings and faults
- Last successful synchronization
- Data freshness

Keep simulated and real telemetry clearly separated.

Allow the user to view either the simulator or real Growatt installation without mixing their statistics.

Do not present an incomplete installation total as authoritative.

## 7. Special Monitoring Scenario

Document and, where supported by verified data, detect this scenario:

- Grid outage occurs.
- Battery discharges to Low Battery.
- One inverter stops reporting.
- Grid returns.
- Master recovers but Slave remains offline.

Display the state and preserve relevant historical data.

Do not implement remote restarts, inverter configuration changes, or other control operations.

## 8. Verification

Run:

- .NET Release build
- Backend tests
- Frontend production build
- Frontend tests
- MySQL persistence checks
- Limited real Growatt API validation
- At least one live polling cycle if supported

Verify that the simulator remains unaffected.

Do not modify databases other than `homeautomation`.

Do not change inverter or BMS settings.

## 9. Documentation

Update the Growatt integration guide and local configuration instructions.

Save this complete prompt in the next sequential file under `docs/prompts/`.

Update the prompt history index.

Document the confirmed measurements, limitations, and remaining unknowns.

## 10. Final Report

Provide:

1. Whether live polling was activated.
2. Confirmation of Master and Slave mapping.
3. Confirmed shared battery data source.
4. Available real telemetry fields.
5. Measurements that remain unavailable.
6. Backend and frontend verification results.
7. Database persistence verification.
8. Exact instructions for viewing real Growatt data in the dashboard.
9. Remaining risks and recommended next steps.

Commit completed changes with a descriptive Git commit message.

Do not push to GitHub automatically.

Prioritize reliable, read-only monitoring and correct data interpretation over implementing additional features.

## Execution summary

- Verified fresh V4 readings for the two confirmed physical roles. Configured the Master as the sole source of shared SOC and voltage in local User Secrets and activated 300-second polling. The inverter serials are redacted in this archive.
- Confirmed a browser-independent poll in MySQL 8.0.46. It wrote 22 Growatt samples across the confirmed Master, Slave, and shared-battery bindings. The simulator had 23 distinct recent series. Source and receipt times were present separately. There were zero Growatt installation-total series.
- Added the separate Live Growatt view, including source and sync times, AC output voltage, inverter load and AC charging power, warnings, faults, and an asymmetric recovery notice. Growatt reported offline for both inverters despite recent samples. Grid and ATS state, signed shared battery power and site totals remain unverified.
- The Release build passed without warnings, 31 backend tests and five frontend tests passed, and the frontend production build passed. Chrome reached sign-in on the live route and the proxied endpoint returned 401. Authenticated browser verification requires the provisioned viewer.

See the [Growatt guide](../integrations/growatt.md), [V4 mapping](../../src/backend/HomeAutomation.Infrastructure/Growatt/GrowattCloudSource.cs), and [Live Growatt screen](../../src/frontend/home-automation-web/src/features/GrowattPanel.tsx).
