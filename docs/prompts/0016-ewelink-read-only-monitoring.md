# Milestone 4A — eWeLink Cloud Discovery & Read-only Monitoring

Date: 2026-11-22

## Original prompt

# Milestone 4A — eWeLink Cloud Discovery & Read-only Monitoring

## Context

Continue development of the existing HomeAutomation repository.

Current stack:
- .NET 10 backend, Dapper, MySQL
- React / TypeScript frontend
- Property-scoped authentication and authorization
- Existing Growatt and Daikin integrations
- Latest Daikin milestone committed as `05298c8`

Follow AGENTS.md, existing architecture, project conventions and installed development skills.

The eWeLink Personal Developer account has been submitted and is currently awaiting approval. API credentials are not yet available.

## Goal

Implement the foundation for an official eWeLink Cloud integration, focusing on device discovery and read-only monitoring.

The account contains several Wi-Fi Sonoff devices, including:
- Standard ON/OFF relays
- Two temperature-controlled relays
- One of those also measures humidity

Both temperature controllers already have autonomous automation rules. These must remain untouched.

## Requirements

### 1. Official API integration

- Use the official eWeLink Developer OAuth/API approach.
- Consult the official API documentation before implementing protocol details.
- Do not use unofficial password-based login, reverse-engineered authentication or hardcoded third-party credentials.
- Do not confuse eWeLink Cloud API with eWeLink CUBE API.
- Implement OAuth authorization, callback, token storage and refresh only to the extent supported by the verified API documentation.
- Store credentials securely using the existing application patterns.
- Never log access tokens, refresh tokens, client secrets or authorization codes.
- If official API details cannot be verified, document the uncertainty rather than inventing endpoints.

### 2. Device discovery

Discover accessible devices and map them to the existing provider/device architecture.

Support:
- Stable external device IDs
- Device name and model, when available
- Online/offline state
- Property and room assignment
- Supported capabilities
- Last successful synchronization

Make discovery idempotent and prevent duplicate devices.

### 3. Read-only monitoring

Expose available capabilities through the existing monitoring pipeline:

- Relay ON/OFF status
- Temperature in °C
- Relative humidity in %
- Online/offline status
- Automation mode and configured thresholds only if exposed by the official API

Capabilities must be discovered from real provider data, not assumed from device names or models.

Handle missing, unsupported or stale measurements gracefully.

### 4. Safety

This milestone is strictly read-only.

- No relay switching
- No thermostat setpoint changes
- No automation-rule changes
- No cloud write endpoints
- No automatic actions affecting physical devices

Existing Sonoff automation must continue operating independently of HomeAutomation.

### 5. Polling and resilience

- Respect the documented API limits.
- Use conservative, configurable polling intervals.
- Support token refresh and expired credentials.
- Handle rate limiting, connectivity loss and partial responses.
- Avoid aggressive retries.
- Preserve the most recent readings with appropriate freshness indicators.

### 6. Frontend

Integrate eWeLink into the existing application UI.

- Connection status
- Connect/disconnect workflow where supported
- Device discovery
- Property and room assignment
- Read-only device cards
- Temperature, humidity and relay status where available
- Clear unsupported/unavailable states

Reuse existing components and patterns rather than introducing a new dashboard design.

### 7. Testing

Use mocked provider responses to test:

- OAuth and token handling
- Device discovery and deduplication
- Device capability mapping
- Temperature and humidity parsing
- Relay status parsing
- Missing or malformed values
- Offline devices
- API failures and rate limiting
- Property authorization

Do not require real eWeLink credentials to run tests.

Run relevant backend and frontend builds and tests.

## Implementation approach

Because Developer API approval is still pending:

1. Inspect the existing architecture and official documentation.
2. Implement only features supported by verified API contracts.
3. Keep unverified provider-specific functionality isolated and clearly documented.
4. Do not invent API response fields or endpoints.
5. Do not attempt live authentication.
6. Do not modify existing Growatt or Daikin behavior.

## Deliverables

- eWeLink integration foundation
- OAuth and discovery implementation where documentation permits
- Read-only monitoring support
- Automated tests
- Updated integration documentation
- A concise checklist for configuring real credentials after approval
- Save this prompt in the existing docs/prompts history

Commit verified changes locally. Do not push.

Keep the implementation focused and avoid unrelated refactoring.

At completion, report implemented features, unsupported or unverified API details, test results, commit hash and exact next steps for connecting the approved Developer account.

## Execution summary

Implemented CoolKit Cloud consent, signed exchange and refresh, encrypted token storage, paged home and device discovery, property and room assignment, and read-only relay and reachability telemetry. Added a property-scoped React view and a bounded polling worker. Temperature, humidity, multi-channel relay state, and thermostat settings await a verified device-specific Cloud contract.

The .NET Release and frontend production builds passed. All 86 backend and 13 frontend tests passed. The additive migration was reviewed in the repository. Live OAuth, polling, and database deployment remain pending the approved Developer credentials and installation setup.

- [eWeLink setup and integration contract](../integrations/ewelink-cloud.md)
- [eWeLink migration](../../database/migrations/004_ewelink_cloud.sql)
- [eWeLink protocol and mapping tests](../../tests/HomeAutomation.UnitTests/EwelinkTests.cs)
