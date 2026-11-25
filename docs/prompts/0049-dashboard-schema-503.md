# HomeAutomation — Diagnose Dashboard HTTP 503 after migrations 012/013

Date: 2026-11-25

## Original prompt

# HomeAutomation — Diagnose Dashboard HTTP 503 after migrations 012/013

The monitoring Dashboard GET endpoint returns HTTP 503 with the message "The dashboard database migration is required."



Verified:

- Migrations 012 and 013 were executed.
- `dashboard_widgets` contains `width_columns` and `measurement_columns`.
- `dashboard_values` contains `color_mode` and `custom_color`.
- `home_runtime` has SELECT, INSERT and UPDATE on `homeautomation.*`.
- Backend starts normally, and other integrations work.
- Backend stdout/stderr logs do not show the underlying Dashboard error.

Tasks:

1. Inspect the Dashboard GET endpoint, repository SQL and exception handling.
2. Identify the exact conditions that produce HTTP 503.
3. Compare all SQL queries with the actual schema after migrations 012/013.
4. Check whether a missing database permission, column, table, constraint or schema-version check can trigger this response.
5. Improve server-side logging to capture the underlying exception without exposing credentials or sensitive data.
6. Fix the confirmed root cause with the smallest possible change.
7. Add a focused regression test.
8. Run relevant tests and build.
9. Archive the prompt and commit locally without pushing.

Do not rerun migrations, alter the live database or broaden database permissions without identifying the specific requirement.



Report the exact root cause and any necessary manual SQL operation.

## Execution summary

- A read-only audit using the local runtime account found all four columns and all three enforced checks from migrations 012 and 013. Both dashboard repository reads succeeded for property 1. The readiness check still failed because `schema_migrations` lacked both embedded script names.
- The API now logs missing migration names and safe MySQL error codes. The dashboard distinguishes incomplete migration history from a failed database read. The [README](../../README.md#configurable-monitoring-dashboard) provides the SQL statement to record both migrations after verifying the applied schema. The live database was left untouched.
- The MySQL 8.0.46 integration regression reproduced the 503 with the columns present and verified HTTP 200 after restoring the ledger rows. It also checks that a repository error produces a distinct, sanitized response. The Release backend and production frontend builds passed. Backend tests recorded 163 passes and nine skips for other opt-in MySQL fixtures. All 44 frontend tests passed.
