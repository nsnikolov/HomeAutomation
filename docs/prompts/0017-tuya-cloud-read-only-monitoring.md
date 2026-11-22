# Milestone 5A — Smart Life / Tuya Cloud Integration

Date: 2026-11-22

## Original prompt

# Milestone 5A — Smart Life / Tuya Cloud Integration

Continue the existing HomeAutomation repository from the current main branch.



Stack: .NET 10, Dapper, MySQL, React/TypeScript.



Follow AGENTS.md, repository architecture, development skills and existing conventions.



Existing integrations:

- Growatt Cloud
- Daikin ONECTA
- eWeLink Cloud (Milestone 4A, commit 089e88e)

## Goal

Implement official Tuya Cloud integration for Wi-Fi devices currently managed through the Smart Life mobile app.



The initial milestone is strictly READ-ONLY.

## Requirements

1. Inspect existing provider integrations and reuse their architecture.
2. Consult current official Tuya Cloud API documentation.
3. Support Tuya Cloud project credentials, regional API endpoint and authenticated requests using the documented Tuya signature/token mechanism.
4. Support device discovery through an authorized Smart Life account linked to the cloud project.
5. Implement idempotent discovery and property/room assignment.
6. Discover capabilities from actual device specifications and status data, including:
   - Online/offline
   - Switch ON/OFF
   - Temperature
   - Humidity
   - Battery level, if available
   - Other supported sensor measurements
7. Handle Tuya datapoint codes and device-specific units/scaling correctly. Never guess units or scaling.
8. Implement conservative polling, rate-limit handling, stale-data indicators and diagnostics.
9. Integrate device cards into the existing React dashboard.
10. Keep all operations read-only: no relay switching, automation modification or physical commands.
11. Respect existing authentication, property isolation and secret-storage conventions.
12. Add database migrations only if required. Never automatically apply migrations to a live database.
13. Add mocked API tests, including signature verification, token expiry, device discovery and measurement parsing.
14. Do not make live device calls or require real credentials during implementation.
15. Document Cloud API pricing, trial expiry and service-access limitations. Do not assume permanent free API access.
16. Document the exact steps to configure and link the Smart Life account when credentials become available.

Run backend/frontend builds and tests.



Archive this prompt in docs/prompts.



Commit verified changes locally. Do not push.



Avoid unrelated refactoring and do not modify working Growatt, Daikin or eWeLink behavior.



Report implementation summary, test results, commit hash, and any API details that require verification against real devices.

## Execution summary

Added regional Tuya request signing and project token renewal, linked-UID discovery, property and room assignment, and read-only Tuya cards. The ingestion path uses device status definitions and applies their explicit units and scale. It keeps distinct relay channels and retains stale values without silently advancing their timestamps. Subscription, quota, and authorization failures surface safe diagnostics. Migration 005 adds Tuya inventory and sync state alongside the existing telemetry tables.

Both production builds passed. All 99 backend and 15 frontend tests passed. Tuya requests used mocked fixtures, and migration 005 was reviewed without applying it to the live database. Actual account access, regional selection, paging, supported datapoint metadata, and telemetry cadence remain to be verified after provisioning.

- [Tuya integration and activation guide](../integrations/tuya-cloud.md)
- [Tuya migration](../../database/migrations/005_tuya_cloud.sql)
- [Tuya protocol and mapping tests](../../tests/HomeAutomation.UnitTests/TuyaTests.cs)
