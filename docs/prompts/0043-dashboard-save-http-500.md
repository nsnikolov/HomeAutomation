# Fix Dashboard Configuration Save — HTTP 500

Date: 2026-11-24

## Original prompt

# Fix Dashboard Configuration Save — HTTP 500

The configurable monitoring dashboard editor is working, but saving the configuration fails.



Observed:

- Property: Countryside House (property 1).
- User has Owner permissions.
- PUT /api/v1/properties/1/dashboard returns HTTP 500.
- Migration 011 has already been applied.
- The dashboard contains a custom group named "СТЪРГЕЛ".
- One widget contains measurements from two different providers:
  - Daikin ONECTA / Sturgel Kitchen / Temperature
  - Smart Life / Tuya / Sturgel kitchen / Temperature

Tasks:

1. Inspect the actual HTTP 500 response and backend exception logs.
2. Reproduce the failure with a focused integration test against MySQL.
3. Identify the root cause, including possible issues with database persistence, cross-provider bindings, Unicode titles, or request validation.
4. Fix the actual cause without weakening property authorization.
5. Ensure multi-provider widgets and Bulgarian titles can be saved.
6. Preserve the editor's unsaved changes when a save fails.
7. Return meaningful validation errors for invalid configurations instead of HTTP 500.
8. Run relevant backend and frontend tests.

Do not change provider polling or device control logic.



Archive the prompt and commit locally without pushing.



Report the exact root cause and fix.

## Execution summary

The live backend log showed that MySQL denied the dashboard store's `DELETE FROM dashboard_groups` statement. The read-only runtime connection resolved to `home_runtime@127.0.0.1` and lacked DELETE. An isolated HTTP test reproduced 500 using the provisioned SELECT, INSERT, and UPDATE grants.

Verified that granting DELETE only on `dashboard_groups` allows an Owner to save and read back the Bulgarian group and mixed Daikin/Tuya temperatures. Clearing the layout cascades through its children without granting direct DELETE on the widget table. Documented the administrator's exact grant. No live grant or dashboard writes were performed.

The API reports missing MySQL permissions as a sanitized 503 and invalid layouts or source bindings as actionable 400 responses. The editor keeps draft inputs after a failed save and can retry. The real MySQL 8.0.46 HTTP regression passed, as did 163 backend tests and 39 frontend tests. Nine unrelated opt-in tests were skipped.

- [Runtime permission setup](../../README.md#configurable-monitoring-dashboard)
- [Dashboard API validation and errors](../../src/backend/HomeAutomation.Api/MonitoringDashboardEndpoints.cs)
