# Milestone 2B — Activate Live Growatt Cloud Monitoring

Date: 2026-11-21

## Original prompt

# Milestone 2B — Activate Live Growatt Cloud Monitoring

Act as a Senior .NET 10 Engineer and IoT Integration Specialist.



We have completed the read-only Growatt integration foundation in commit `43d2de6`.



The user has now generated an API Secret Key token through:



Growatt ShineServer → Settings → Account Management.



The token has been stored locally in .NET User Secrets under:



`Growatt:ApiToken`



Do not ask the user to provide the token in chat.

## 1. Review Existing Implementation

Before making changes:

- Read AGENTS.md and applicable Codex Skills.
- Review docs/architecture/ and docs/integrations/growatt.md.
- Inspect existing Growatt contracts, polling worker, configuration, database mapping, tests, and React GrowattPanel.
- Follow the established prompt-history convention.

Preserve the existing modular monolith and capability-based architecture.

## 2. Verify API Access

Investigate the official Growatt OpenAPI and its supported versions.



Evaluate V1 and V4 only where documented and relevant.



Use the existing token to perform minimal, read-only discovery requests against the appropriate Growatt regional server.



Determine:

- Whether the token is accepted.
- Whether plant discovery is authorized.
- Whether the two SPF 5000 ES inverters are discoverable.
- Which device types the API reports.
- Which read-only telemetry endpoints support these devices.
- API request limits and recommended polling intervals.
- Whether the token permits access to current and historical measurements.

Do not assume that a token accepted for plant discovery also provides SPF inverter telemetry.



If access is denied or unsupported, report the exact sanitized error and implement only independently verifiable functionality.



Do not attempt to bypass authorization or use undocumented write operations.

## 3. Credential Security

Read credentials from .NET User Secrets through the existing configuration system.



Use `Growatt:ApiToken`, or safely migrate to the repository's existing configuration convention if necessary.



Requirements:

- Do not print or log the token.
- Do not put the token in URLs.
- Do not expose the token to React.
- Do not commit credentials.
- Do not include credentials in tests or fixtures.
- Do not persist raw authenticated HTTP traffic.
- Redact sensitive headers, cookies, and account details from diagnostic output.

Keep live monitoring disabled until the configuration and access checks succeed.

## 4. Implement Growatt Cloud Transport

If the API is verified and accessible:

- Implement a typed HTTP client using IHttpClientFactory.
- Implement read-only plant and device discovery.
- Implement supported inverter telemetry retrieval.
- Implement response validation and typed DTO mapping.
- Support cancellation and request timeouts.
- Handle authentication, permission and rate-limit errors explicitly.
- Respect Retry-After or documented provider retry guidance.
- Use bounded retries and backoff.
- Avoid unnecessary API requests.
- Keep the existing simulator fully operational.

Use the existing Growatt application contracts and polling infrastructure rather than creating a parallel architecture.

## 5. Real Hardware Mapping

The property contains:

- Two Growatt SPF 5000 ES inverters in parallel.
- One Master and one Slave.
- One shared 51.2 V / 200 Ah LiFePO4 battery.
- Solar panels and grid connection.

Map each physical inverter separately.



Use stable provider identities.



Prevent duplicate batteries or duplicated installation-level measurements.



Import available measurements, including:

- PV power and voltage.
- Grid voltage and availability.
- Grid import power.
- Household consumption.
- Battery SOC and voltage.
- Battery charging/discharging.
- Inverter operating modes.
- Device online/offline state.
- Warnings and faults.

Only map measurements supported by verified API responses.



Clearly distinguish individual inverter values from installation totals.



Preserve source timestamps and receipt timestamps.



Handle stale and missing values correctly.

## 6. Database and Telemetry

Use the existing MySQL and Dapper infrastructure.



Avoid unnecessary schema changes.



If a schema migration is genuinely required:

- Create a versioned migration.
- Preserve existing simulator and historical data.
- Do not modify databases outside `homeautomation`.
- Verify migration compatibility with MySQL 8.0.46.

Ensure simulated and real telemetry remain separated

## Execution summary

- Verified the existing token on Growatt International. V1 returned one plant and denied device-list access with HTTP 200, API code `10011` and `error_permission_denied`. V4 found two distinct storage devices and returned latest readings for both. A one-day history check returned 287 observations for one serial and none for the other.
- Added read-only discovery and a typed V4 latest-reading adapter through the existing Growatt worker, mapper, and MySQL writer. Preserved the device and receipt timestamps and separate simulator and Growatt series without a schema change. The checks used the provisioned MySQL 8.0.46 database for health and a read-only series count.
- Continuous polling remains disabled. The physical Master and Slave assignments and the authoritative shared-battery source still require confirmation. The activation guide records the observed timezone behavior and the remaining telemetry scope limits.
- Release build passed with no warnings. All 31 backend tests and four frontend tests passed. The frontend production build passed. The updated backend returned HTTP 200 for liveness and readiness and HTTP 401 for an unauthenticated Growatt request.

Related changes are in the [Growatt integration guide](../integrations/growatt.md), [typed cloud client](../../src/backend/HomeAutomation.Infrastructure/Growatt/GrowattCloudClient.cs), [cloud source](../../src/backend/HomeAutomation.Infrastructure/Growatt/GrowattCloudSource.cs) and [backend tests](../../tests/HomeAutomation.UnitTests/GrowattCloudTests.cs).
