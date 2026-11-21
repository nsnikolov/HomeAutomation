# 0004 — Milestone 2 — Growatt Read-Only Integration

**Date** 2026-11-21

## Original prompt

# Milestone 2 — Growatt Read-Only Integration

Act as a Senior .NET Engineer and IoT Integration Architect.

Extend the existing Home Automation application with a production-oriented, read-only Growatt integration.

Review the repository, AGENTS.md, Codex Skills, architecture documents, ADRs, existing simulator implementation, and prompt history before making changes.

Preserve the existing .NET 10 modular monolith, React + TypeScript frontend, MySQL + Dapper persistence, and capability-based device architecture.

## 1. Existing Hardware

The initial property contains:

- Two Growatt SPF 5000 ES off-grid inverters operating in parallel (Master and Slave).
- One shared 51.2 V / 200 Ah LiFePO4 battery.
- BMS communication.
- Approximately 4 kWp of solar panels.
- Grid connection and external ATS.
- ShinePhone monitoring with both inverters visible as separate devices.

The ShinePhone account does not currently expose an API Token.

The application must initially monitor these devices without modifying their configuration.

## 2. Investigate Growatt Integration Options

Before implementing provider-specific communication, research and evaluate:

1. Official Growatt OpenAPI.
2. Growatt ShineServer / ShinePhone integration possibilities.
3. Account eligibility and API token requirements.
4. Existing community integration libraries.
5. Local datalogger and inverter communication alternatives.

Use official technical documentation where available.

Clearly distinguish:

- Officially supported APIs.
- Documented but access-restricted APIs.
- Unofficial or reverse-engineered integrations.
- Unverified assumptions.

Do not invent endpoints, authentication flows, or response formats.

Do not assume that the ShinePhone account can use the official API without authorization.

Document findings in `docs/integrations/growatt.md`, including sources, limitations, and recommended integration approach.

## 3. Implementation Strategy

Implement the provider-neutral Growatt integration infrastructure regardless of whether live API credentials are available.

If a supported and verified API access method is available, implement a read-only Growatt Cloud adapter.

If credentials, permissions, or reliable protocol documentation are unavailable:

- Do not fabricate a working integration.
- Implement the adapter contract, configuration validation, provider models, and test fixtures.
- Keep the existing simulator operational.
- Clearly document what is required to enable live monitoring.

Do not use browser scraping or hardcoded account credentials.

Do not bypass authentication, CAPTCHA, MFA, or provider access restrictions.

## 4. Device Discovery and Identity

Support:

- Multiple Growatt inverters.
- Master and Slave identification.
- One logical solar installation.
- One shared battery.
- Stable provider-specific device identifiers.
- Mapping provider devices to application devices.
- Capability discovery where supported.
- Device online/offline status.
- Last successful observation timestamps.

Avoid creating duplicate logical batteries when both inverters report data about the same battery.

Preserve the existing device and endpoint architecture.

## 5. Telemetry Mapping

Design and implement mapping for available Growatt measurements:

- Grid input voltage and frequency.
- Grid availability.
- Grid import power.
- PV voltage and power.
- Household load.
- Battery voltage.
- Battery SOC.
- Battery charge/discharge power.
- Inverter operating mode.
- Device warnings and faults.
- Device connectivity.

Preserve provider timestamps and ingestion timestamps separately.

Track measurement freshness and quality.

Do not interpret missing values as zero.

Respect the distinction between:

- Measurements reported by an individual inverter.
- Measurements representing the complete installation.
- Shared battery measurements.

Prevent double-counting installation consumption, PV production, and battery energy.

Do not assume that every SPF 5000 ES firmware version exposes identical telemetry fields.

## 6. Polling and Reliability

Implement a provider-specific polling service using existing background worker patterns.

Requirements:

- Configurable polling frequency.
- Rate limiting.
- Bounded retries and exponential backoff.
- CancellationToken support.
- Structured logging.
- Circuit-breaker behavior where appropriate.
- Recovery after temporary cloud outages.
- No overlapping requests.
- Persistent telemetry ingestion.
- Correct stale/offline state transitions.

Polling must continue without an open browser.

Do not allow Growatt failures to stop other device providers.

## 7. Frontend

Extend the existing React dashboard to support live Growatt devices when configured.

Show:

- PV production.
- Battery SOC and voltage.
- Grid availability.
- Household consumption.
- Battery charging/discharging.
- Individual inverter status.
- Master/Slave connectivity.
- Latest warnings and faults.
- Measurement freshness.

Preserve the simulator dashboard.

Clearly distinguish simulated data from real Growatt readings.

If Growatt is not configured, show an informative integration status rather than fake values.

## 8. Security

- Keep provider credentials in backend configuration or secure storage.
- Never expose credentials to React.
- Do not log passwords, tokens, or session cookies.
- Validate external data.
- Use read-only provider operations.
- Do not implement inverter settings changes.
- Do not implement remote inverter restarts.
- Do not implement battery charging controls.
- Do not implement electrical switching.

## 9. Tests

Add focused tests covering:

- Provider response mapping.
- Master/Slave identification.
- Shared battery deduplication.
- Installation-level aggregation.
- Stale and missing telemetry.
- Cloud timeout and connection failures.
- Provider rate limits.
- Recovery after transient errors.
- Simulator and real provider isolation.

Use mocked provider responses and deterministic tests.

Do not require real Growatt credentials for automated tests.

## 10. Documentation and Configuration

Update:

- Growatt integration documentation.
- Relevant architecture documents if implementation decisions change.
- README.md with setup instructions.
- .env.example where appropriate.

Do not modify the existing MySQL databases outside `homeautomation`.

Use versioned migrations for any required schema changes.

Do not execute destructive database operations.

## 11. Prompt History

Follow AGENTS.md.

Save this complete prompt as the next sequential file under `docs/prompts/`.

Update the prompt index and record the execution summary.

Do not overwrite previous prompts.

## 12. Completion Criteria

Before completion:

1. Build the .NET solution.
2. Build the React frontend.
3. Run all existing tests.
4. Run new integration tests.
5. Verify simulator functionality remains intact.
6. Verify database migration compatibility.
7. Document any unavailable Growatt API access.
8. Identify remaining requirements for connecting the real ShinePhone account.

## 13. Final Report

Provide:

- Integration research findings and sources.
- Chosen integration strategy and justification.
- Implemented components.
- Remaining blockers.
- Required user setup steps.
- Build and test results.
- Changed files.
- Recommended next milestone.

Commit the completed changes with a descriptive Git commit message.

Do not push to GitHub automatically.

Prioritize a reliable, maintainable integration over achieving live connectivity through undocumented or fragile mechanisms.

Do not stop after research. Implement all safe provider-neutral and testable components that do not require credentials.

## Execution summary

Researched Growatt Cloud, installer-managed access, the ShinePhone and ShineServer ecosystem, and community and local alternatives. Implemented a normalized read contract, explicit inverter and battery identities, scoped mapping and aggregation, an isolated polling worker, MySQL source separation, and an authenticated Growatt dashboard panel. Live activation requires authorized SPF 5000 ES coverage, the provider contract, and a verified transport implementation.

Both builds passed with 19 backend and 4 frontend tests. A separate MySQL 8.0 fixture passed fresh and repeat migrations, simulated and synthetic Growatt persistence isolation, sample deduplication, and separate receipt timestamps. The restarted local API reported healthy readiness and simulator persistence through the existing application connection. Growatt schema changes were unnecessary.

- [Integration research and enablement prerequisites](../integrations/growatt.md)
- [Normalized read contract](../../src/backend/HomeAutomation.Application/Growatt.cs)
- [Growatt polling worker](../../src/backend/HomeAutomation.Api/GrowattWorker.cs)
- [Growatt dashboard panel](../../src/frontend/home-automation-web/src/features/GrowattPanel.tsx)
