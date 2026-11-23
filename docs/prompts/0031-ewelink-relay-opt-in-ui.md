# eWeLink per-device relay enablement

Date: 2026-11-23

## Original prompt

Investigate why eWeLink ON/OFF controls are not visible after Milestone 4E (commit c87eddc).

Confirmed: Ewelink:EnableWrites=true in .NET User Secrets. PSF-X67 devices are online and assigned, but their cards display no control buttons or per-device enablement settings.

Inspect the actual backend/frontend implementation and identify the exact blocker.

Ensure operators can enable writes individually for each PSF-X67 device through the UI, with a clear disabled-state explanation and ON/OFF buttons after opt-in.

Preserve all security checks, confirmation requirements for Inverter In/Out, command auditing, and readback verification. Keep THR320D read-only.

Add focused tests, run builds, archive the prompt and commit locally. Do not push or send live device commands.

## Execution summary

The old backend required a separate `Ewelink:RelayWriteDevices` entry for each device, and the frontend only rendered controls when that entry was enabled. A read-only check confirmed migration 008 and UIID 32 relay metadata for all five assigned PSF-X67 devices, with no per-device opt-ins in local User Secrets or this process's environment.

Added a property-scoped, CSRF-protected preference endpoint and enable/disable actions on eligible cards. Migration 009 stores the preference against the stable device ID. An unset preference falls back to legacy configuration. Protected relay confirmation, command audits, readback, and the THR320D read-only policy remain in place.

Both builds passed. Backend tests recorded 131 passes and three opt-in Tuya MySQL skips, including preference persistence and mocked relay readback on disposable MySQL 8.0.46. All 29 frontend tests passed. No live commands or live migration were performed.

See the [operator guide](../integrations/ewelink-cloud.md#manual-relay-control) and [migration 009](../../database/migrations/009_ewelink_relay_preferences.sql).
