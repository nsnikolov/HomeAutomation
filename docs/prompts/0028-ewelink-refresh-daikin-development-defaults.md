# Small Enhancement — eWeLink Refresh and Daikin Development Defaults

Date: 2026-11-23

## Original prompt

# Small Enhancement — eWeLink Refresh and Daikin Development Defaults

Continue the existing HomeAutomation repository.

## 1. eWeLink polling

- Change the default eWeLink polling interval from 30 to 5 minutes.
- Update configuration validation to permit 5 minutes.
- Update documentation and tests.
- Preserve API quota, rate-limit, cooldown, backoff and retry protections.
- If the configured polling interval is overridden by User Secrets or environment variables, document how to update the effective value.
- Do not change polling for Growatt, Daikin or Tuya.

## 2. Force Refresh button

Add a clearly labeled "Force Refresh" action to the eWeLink page.



Requirements:

- Trigger a fresh provider discovery and telemetry synchronization, not merely reload cached UI data.
- Reuse the existing authenticated refresh endpoint and service pipeline where possible.
- Only authorized operators may trigger it.
- Preserve CSRF and property authorization.
- Do not bypass active provider throttling, quota exhaustion or enforced backoff.
- Prevent concurrent or repeated refresh requests.
- Display the actual result: succeeded, deferred, rate-limited or failed.
- Show last attempted and last successful synchronization times.
- Ensure successful discovery writes inventory diagnostics when Ewelink:InventoryDiagnostics is enabled.
- Do not send physical device commands.

## 3. Daikin default for local development

The operator wants Daikin manual writes enabled by default in the local Development environment, without setting Daikin__EnableWrites=true every time.



Requirements:

- Enable Daikin:EnableWrites by default only in the local Development configuration.
- Keep Production default disabled.
- Preserve explicit environment-variable and User Secrets overrides.
- Preserve operator authorization, property checks, command audits, readback verification, quota protections and Altherma read-only restrictions.
- Do not modify physical devices during implementation.
- Document how to explicitly disable Daikin writes when needed.

## 4. Verification

- Add focused backend and frontend tests.
- Verify Development and Production configuration behavior.
- Run relevant builds and tests.
- Update integration documentation and archive this prompt.
- Commit locally. Do not push.

Keep the implementation small and avoid unrelated refactoring.

## Execution summary

Changed the eWeLink default and minimum poll interval to five minutes, and added Force Refresh to the existing authorized refresh endpoint. It runs fresh discovery and ingestion while retaining provider and failure cooldowns. Concurrent attempts are deferred and a one-minute guard prevents rapid repeats. The page shows the result and persisted last attempt and successful synchronization times.

Migration 007 adds the attempt timestamp and regional request counters. The app reserves capacity for discovery, token calls, and replays before sending requests. Development now defaults to enabled Daikin manual writes and Production defaults to disabled. Explicit configuration overrides and the existing command pipeline are preserved.

Both builds passed. Backend tests recorded 123 passed and three opt-in Tuya MySQL skips. Tests included eWeLink persistence and quota rollover on disposable MySQL 8.0.46. All 27 frontend tests passed. No live migration, provider requests, or device commands were performed.

See the [eWeLink integration guide](../integrations/ewelink-cloud.md#polling-and-limits), [Daikin guide](../integrations/daikin-onecta.md#connect-the-local-account), and [migration 007](../../database/migrations/007_ewelink_sync_budget.sql).
