# Milestone 5B — Historical Charts and Dashboard UX

Date: 2026-11-22

## Original prompt

# Milestone 5B — Historical Charts and Dashboard UX

Continue development of the existing HomeAutomation repository.



Current stack:

- .NET 10 / Dapper / MySQL
- React / TypeScript
- Property-scoped authentication
- Existing Growatt, Daikin ONECTA, eWeLink and Tuya integrations

Latest Tuya hotfix: commit `97b6877`.



Follow AGENTS.md, existing architecture, established development skills and repository conventions.

## Goal

Improve device monitoring and dashboard usability without changing physical device behavior.



Implement four features:

### 1. Historical temperature and humidity charts

First inspect the existing telemetry persistence and history APIs.



Reuse existing infrastructure wherever possible.



Requirements:

- Historical charts for temperature and relative humidity.
- Selectable periods: 24 hours, 7 days and 30 days.
- Support Tuya devices initially, with a reusable design for other providers.
- Use MySQL for persistence.
- Store only readings with trustworthy timestamps.
- Do not generate artificial samples from unchanged or undated cloud snapshots.
- Avoid duplicate records from repeated polling.
- Show gaps when historical measurements are unavailable.
- Use efficient, bounded history queries.
- Keep charts responsive on desktop and mobile.
- Display units and timestamps clearly.

### 2. Persistent drag-and-drop ordering

Allow users to rearrange device cards using drag and drop.



Apply to existing device-card pages, including:

- Dashboard, where applicable
- Growatt
- Daikin ONECTA
- eWeLink
- Smart Life / Tuya
- Devices overview

Requirements:

- Inspect existing card components before implementing.
- Only enable ordering in actual device-card collections; do not reorder unrelated controls, charts or navigation.
- Persist ordering in MySQL.
- Scope ordering by property, page and authenticated user.
- Preserve ordering after refresh, logout and login.
- Support touch devices and an accessible keyboard alternative.
- Avoid interfering with existing buttons, selectors and manual device controls.
- New devices should appear after previously ordered devices within their group.
- Do not make ordering dependent on cloud provider synchronization.

### 3. Assigned devices before unassigned devices

Across all relevant device-card pages:

- Show devices assigned to the current property first.
- Display unassigned devices afterward in a clearly labeled section.
- Preserve user-defined ordering within each section.
- Never mix devices from different properties.
- Do not expose devices assigned to another property.
- Handle empty sections gracefully.
- Keep assignment controls functional.

### 4. Hide and restore Tuya devices

Add a small, unobtrusive Hide control on each Tuya device card.



Requirements:

- Hide is a local HomeAutomation presentation setting.
- Never delete the device from Tuya Cloud.
- Never delete local device records or historical telemetry.
- Never alter provider credentials or physical automation.
- Hidden devices should not appear in the normal Tuya device list.
- Add a compact "Show hidden (N)" control.
- When enabled, display hidden devices in a separate section.
- Each hidden device must have a Restore action.
- Hidden status must persist across refreshes and synchronization.
- Scope visibility preferences by property and authenticated user.
- Newly discovered devices should be visible by default.
- Hide/restore must not interfere with property assignment.
- Ensure hidden devices do not reappear automatically after cloud rediscovery.

## Safety and data integrity

- Do not send physical device commands.
- Preserve Daikin manual command authorization and its current operator configuration.
- Preserve existing Growatt, Daikin, eWeLink and Tuya integrations.
- Use additive database migrations only when necessary.
- Do not run migrations against the live database.
- Do not access or print real credentials.
- Enforce property authorization on all new endpoints.
- Use existing CSRF and authentication patterns.

## Tests

Add focused backend and frontend tests for:

- Historical telemetry deduplication.
- Missing and stale timestamps.
- History filtering and property isolation.
- Persistent ordering.
- Assigned-before-unassigned grouping.
- Hidden device persistence.
- Restore functionality.
- Cloud rediscovery preserving hidden state.
- Unauthorized preference modifications.

Run relevant backend/frontend builds and tests.

## Documentation and delivery

- Update relevant architecture and integration documentation.
- Save this prompt in docs/prompts.
- Commit verified changes locally.
- Do not push.
- Do not apply migrations to the live database.
- Do not send real physical commands.

Keep the implementation focused. Reuse existing patterns and avoid unrelated refactoring.



Report:

1. Implemented features.
2. Any limitations.
3. Required database migration.
4. Test results.
5. Commit hash.
6. Steps to activate and verify the changes locally.

## Execution summary

Added Tuya temperature and humidity history from individually timestamped cloud reports, with bounded 24-hour, 7-day and 30-day charts. Added touch and keyboard ordering to the existing device collections, assigned-first grouping, and persistent local hide/restore for Tuya. Migration 006 stores per-user, per-property and per-page preferences. Provider synchronization continues independently.

Both builds passed. All 105 backend and 19 frontend tests passed, including real MySQL 8.0.46 tests for deduplication, history filtering, property and user isolation, and persistence after rediscovery. Migrations were exercised only on a disposable MySQL instance. No live provider calls, credential access or physical commands were used.

- [Activation and Tuya history guide](../integrations/tuya-cloud.md#temperature-and-humidity-history)
- [Device-view preferences migration](../../database/migrations/006_device_view_preferences.sql)
- [MySQL history and preference tests](../../tests/HomeAutomation.UnitTests/TuyaMySqlTests.cs)
