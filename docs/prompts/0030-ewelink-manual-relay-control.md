# Milestone 4E — eWeLink Relay ON/OFF Control

Date: 2026-11-23

## Original prompt

# Milestone 4E — eWeLink Relay ON/OFF Control

Continue the existing HomeAutomation repository from the latest commit.



Implement manual relay ON/OFF control for supported eWeLink devices.

## Requirements

1. Use the official CoolKit OAuth v2 API:\
   POST /v2/device/thing/status\
   Follow the documented request format and existing authenticated client architecture.
2. Support single-channel switch control for verified devices:
   - PSF-X67 (UIID 32)
   - THR320D (UIID 181), only if the exact protocol supports its switch control.
3. Add ON/OFF controls to eWeLink device cards.
   - Display actual reported relay state.
   - Disable controls when offline, unavailable, stale, unauthorized or already executing.
   - Use explicit desired state (ON or OFF), never an unverified toggle.
   - Do not optimistically report success.
4. After a command:
   - Confirm provider acceptance.
   - Read back device state within a bounded verification window.
   - Display Confirmed, Pending, Failed or Unverified accurately.
   - Refresh the card and persist the resulting telemetry.
5. Security and safety:
   - Manual actions only; no scheduling or automation.
   - Property-scoped operator authorization and CSRF protection.
   - Add an explicit per-device write-enabled setting, default OFF.
   - Devices named "Inverter In" and "Inverter out" must remain protected by default, with an additional confirmation for every command.
   - Do not assume names are permanent identifiers: store safety settings against stable internal device IDs.
   - Never automatically retry a physical command after an ambiguous timeout.
   - Do not perform bulk switching.
   - Preserve existing provider rate limits and cooldowns.
   - Audit actor, device, requested state, timestamp and result without recording secrets.
6. Configuration:
   - Add Ewelink:EnableWrites, default false.
   - Enabling writes must not automatically authorize individual devices.
   - Existing Daikin functionality must remain unchanged.
7. Tests and delivery:
   - Use mocked provider responses; no live switching during implementation.
   - Test authorized/unauthorized commands, offline devices, unsupported capabilities, protected devices, timeout, failed readback and concurrent commands.
   - Run backend/frontend builds and tests.
   - Document operator setup and usage.
   - Archive the prompt and commit locally.
   - Do not push.

Keep implementation focused and consistent with the existing architecture.

## Execution summary

Added explicit ON/OFF commands for owned, assigned PSF-X67 relays reporting UIID 32. Writes require global and stable-ID per-device opt-in, property-scoped operator access, CSRF validation, and fresh relay state. Inverter confirmation follows the stored device record across renames. THR320D remains read-only because the exact UIID 181 write contract could not be verified.

The authenticated CoolKit client sends one POST and reads the reported state with a bounded GET after acceptance. Audit records distinguish pending, confirmed, failed, and unverified outcomes. An unresolved command blocks another write until a subsequent inventory read reconciles it. Migration 008 adds the durable audit and active-command uniqueness constraint.

Both builds passed. Backend tests recorded 131 passes and three opt-in Tuya MySQL skips, including mocked relay readback, resulting telemetry persistence, and the audit migration on disposable MySQL 8.0.46. All 28 frontend tests passed. No live provider calls, physical switching, or live migration were performed.

See the [operator setup](../integrations/ewelink-cloud.md#manual-relay-control), [relay command handling](../../src/backend/HomeAutomation.Infrastructure/Ewelink/EwelinkRelayControl.cs), and [migration 008](../../database/migrations/008_ewelink_relay_commands.sql).
