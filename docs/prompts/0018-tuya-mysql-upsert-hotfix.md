# Hotfix — Tuya discovery fails with ambiguous MySQL column

Date: 2026-11-22

## Original prompt

# Hotfix — Tuya discovery fails with ambiguous MySQL column

Milestone 5A is committed as `8345e74`.



The first live Tuya startup and manual discovery fail with:



MySqlConnector.MySqlException:\
Column 'last_success_utc' in field list is ambiguous



Stack trace:

- HomeAutomation.Infrastructure.Tuya.TuyaStore.ReportAsync
- TuyaStore.cs:28
- TuyaService.SyncAsync:121

TuyaWorker repeatedly logs:\
"Tuya poll deferred with MySqlException."

## Task

1. Inspect the exact SQL statement in TuyaStore.ReportAsync and migration 005.
2. Fix the ambiguous `last_success_utc` reference using valid MySQL syntax.
3. Preserve the intended semantics:
   - Successful synchronization updates last-success time.
   - Failed synchronization preserves the previous successful timestamp.
   - Next attempt and error status remain correct.
4. Check for similar SQL ambiguity elsewhere in the Tuya store.
5. Add a focused regression test against MySQL, if the existing test infrastructure supports it. Mock-only tests are insufficient to prove SQL compatibility.
6. Run the relevant tests and backend build.
7. Do not alter Tuya credentials, database schema, or other integrations.
8. Do not send any physical device commands.
9. Commit the verified fix locally. Do not push.

Keep the change minimal and report the commit hash and verification results.

## Execution summary

Qualified the existing-row timestamp in the Tuya sync upsert. The other Tuya store statements have no equivalent ambiguous reference. The schema and credentials were preserved.

The opt-in regression reproduced the original error on a disposable MySQL 8.0.46 instance. After the fix, it verified the initial null state, preservation on failure, and replacement on recovery with the correct retry time and status. The Release build passed without warnings, and all 100 backend tests passed.

- [Tuya sync upsert](../../src/backend/HomeAutomation.Infrastructure/Tuya/TuyaStore.cs)
- [MySQL regression](../../tests/HomeAutomation.UnitTests/TuyaMySqlTests.cs)
