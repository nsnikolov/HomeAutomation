# Milestone 3 — Daikin ONECTA Cloud Integration

Date: 2026-11-21

## Original prompt

# Milestone 3 — Daikin ONECTA Cloud Integration

Act as a Senior .NET 10 Engineer, IoT Integration Architect, and React Engineer.

Implement the initial Daikin ONECTA integration in the existing Home Automation application.

The objective is to discover and monitor real Daikin air conditioners and prepare safe, manually initiated device control through the official ONECTA Cloud API.

Review the existing repository, AGENTS.md, relevant Codex Skills, architecture documents, ADRs, device capability model, Growatt integration, and prompt history.

Preserve the existing technology stack and modular monolith architecture.

## 1. Context and Hardware

The application currently supports:

- .NET 10 backend
- React + TypeScript frontend
- MySQL with Dapper
- Background telemetry workers
- SignalR
- Simulated devices
- Live read-only Growatt V4 integration

The countryside property contains Daikin air conditioners managed through the ONECTA mobile application.

One Daikin is used for maintaining the building at approximately 16°C when unoccupied.

A supported low-temperature heating mode or 10°C setpoint is used for reducing heating demand during extended grid outages.

We also want to support additional Daikin devices and properties in the future.

Do not hardcode device models, account identifiers, property names, or temperature limits.

## 2. Official API Investigation

Research and verify the current Daikin ONECTA Cloud API documentation.

Identify:

- Developer Portal registration requirements.
- OAuth2 authorization flow.
- Client ID and Client Secret requirements.
- Redirect URI configuration.
- Token refresh behavior.
- Available device discovery endpoints.
- Supported monitoring capabilities.
- Supported control capabilities.
- API quotas and rate limits.
- Device-specific limitations.

Use official Daikin documentation wherever possible.

Clearly distinguish verified capabilities from assumptions.

Do not use undocumented mobile application endpoints or reverse-engineered authentication.

Do not assume that every Daikin device supports identical control features.

Document findings in:

`docs/integrations/daikin-onecta.md`

## 3. Authentication and Account Connection

Implement the official OAuth2 authorization flow using backend-managed authentication.

Requirements:

- Secure authorization-code flow.
- PKCE if supported or required by the provider.
- State validation against CSRF.
- Correct redirect URI handling.
- Secure Client ID and Client Secret configuration.
- Secure storage of access and refresh tokens.
- Automatic token refresh.
- Handling expired or revoked credentials.
- Account disconnection and credential revocation where supported.
- No tokens exposed to React.
- No credentials in Git, logs, or API responses.

The existing application authentication must remain active.

Only authorized users may connect or disconnect provider accounts.

If ONECTA developer credentials are not available, implement all independently testable components without pretending authentication succeeded.

Document the exact user setup steps.

## 4. Device Discovery

Implement discovery of Daikin devices authorized under the connected ONECTA account.

Support:

- Multiple Daikin devices.
- Stable provider device identifiers.
- Device names.
- Device models where available.
- Online/offline state.
- Property and room assignment.
- Discovery refresh.
- Device capability metadata.

Reuse the existing provider-neutral device and endpoint model.

Do not create duplicate devices on repeated discovery.

Do not assume all discovered devices belong to the same property.

## 5. Monitoring

Read supported device measurements and states:

- Power on/off.
- Operating mode.
- Current temperature.
- Target temperature.
- Indoor temperature.
- Outdoor temperature where available.
- Fan speed.
- Humidity where available.
- Energy consumption where available.
- Device connectivity.
- Last observation timestamp.

Only expose measurements actually supported by each device.

Distinguish:

- Actual device state.
- Desired device state.
- Unsupported features.
- Unavailable measurements.
- Stale measurements.

Preserve provider timestamps separately from ingestion timestamps where supplied.

Do not invent measurement values.

## 6. Polling and Quota Management

Implement an independent Daikin polling service.

Requirements:

- Configurable polling frequency.
- Conservative default polling interval.
- Request quota tracking.
- Daily quota reserve for manual operations.
- Retry and backoff handling.
- CancellationToken propagation.
- No overlapping polling.
- Recovery from transient cloud failures.
- Correct stale/offline transitions.
- No impact on Growatt or simulator workers.

The official ONECTA API may have a relatively restrictive daily request quota.

Verify the current quota from official documentation or the authenticated developer account before selecting a polling interval.

Account for:

- Device discovery requests.
- Periodic status reads.
- Manual control commands.
- Command verification requests.
- Authentication and refresh overhead where applicable.

Avoid consuming the entire daily quota through polling alone.

## 7. Manual Device Control

Design and implement the backend command infrastructure for supported Daikin controls.

Potential commands:

- Power on/off.
- Set heating or cooling mode.
- Set target temperature.
- Set fan speed where supported.

Requirements:

- Validate commands against discovered device capabilities.
- Enforce device-specific temperature limits.
- Use authorized, explicitly initiated commands.
- Track command requests and outcomes.
- Prevent unnecessary repeated commands.
- Avoid conflicting concurrent commands.
- Handle ambiguous provider timeouts.
- Verify resulting device state when supported.
- Maintain an audit trail.

Do not assume HTTP success means the physical device has applied the command.

For this milestone, implement the command handlers, validation, authorization, tests, and frontend controls.

Do not enable real command execution automatically.

Keep real writes disabled behind an explicit feature flag until the user authorizes a controlled manual test.

Read-only operation must remain functional independently of write permissions.

## 8. React Interface

Add a Daikin section to the existing application.

Display:

- Connected account status.
- Discovered devices.
- Current power status.
- Operating mode.
- Current and target temperatures.
- Connectivity status.
- Last successful synchronization.
- Data freshness.
- Supported capabilities.

Provide reusable controls for:

- Power on/off.
- Heating/cooling mode.
- Target temperature.
- Fan speed where supported.

Controls must be disabled when:

- Authentication is unavailable.
- Commands are not authorized.
- The device does not support the requested capability.
- Telemetry is too stale for safe execution.
- The integration is in read-only mode.

Show pending, successful, failed, and uncertain command states distinctly.

Do not create a completely separate frontend architecture for Daikin.

## 9. Future Automation Compatibility

Prepare the integration for a later Automation Engine milestone.

Reference scenario:

When the countryside house is unoccupied:

- Normal operation: Daikin maintains 16°C.
- Confirmed grid outage: reduce heating to 10°C.
- Stable grid recovery: restore 16°C.
- Arrival mode: user may manually request 21–22°C.

These are example policies, not commands to execute now.

Important requirements:

- Manual overrides take precedence.
- Grid state must be verified independently.
- Stale Growatt telemetry cannot prove grid availability.
- Internet outage behavior must be explicitly defined.
- Frost protection must not depend solely on cloud automation.
- An unavailable Daikin API must not trigger repeated commands.

Do not implement or activate this automation in Milestone 3.

## 10. Database

Use existing MySQL and Dapper infrastructure.

Persist:

- Provider account connections.
- Device bindings.
- Device capabilities.
- Relevant current state.
- Historical telemetry.
- Command audit information where required.

Use versioned SQL migrations for schema changes.

Preserve existing data.

Do not modify any database outside `homeautomation`.

Do not store plaintext provider tokens in ordinary database columns.

If persistent token storage is required, use an appropriate encryption mechanism with separately managed keys.

## 11. Security

- Use official OAuth2 authentication only.
- Never store the user's ONECTA password.
- Never expose Client Secret or refresh tokens to React.
- Protect provider-connection endpoints.
- Require authorization for physical device commands.
- Validate all incoming commands.
- Prevent CSRF and unauthorized control.
- Avoid logging sensitive headers or token payloads.
- Keep integration secrets out of source control.

Do not weaken existing authentication to make OAuth development easier.

## 12. Testing

Add deterministic backend and frontend tests covering:

- OAuth state validation.
- Token refresh.
- Token expiration.
- Authentication errors.
- Device discovery.
- Duplicate device prevention.
- Capability mapping.
- Unsupported controls.
- Temperature validation.
- Rate limiting.
- Device offline and stale states.
- Command authorization.
- Ambiguous command outcomes.
- Growatt and Daikin integration isolation.

Use mocked API responses.

Do not require real Daikin credentials for automated tests.

## 13. Verification

Run:

- .NET Release build.
- Backend tests.
- React production build.
- Frontend tests.
- MySQL migration checks.
- Local API smoke checks.

If developer credentials are available, perform authorized read-only discovery and monitoring.

Do not execute real control commands without explicit approval.

Report exactly which checks could not be completed.

## 14. Documentation and Prompt History

Update:

- docs/integrations/daikin-onecta.md
- README.md
- Relevant architecture documents if required
- Example configuration without credentials

Follow AGENTS.md.

Save the complete prompt as the next sequential file under docs/prompts/.

Update the prompt index and execution summary.

## 15. Completion Report

Provide:

1. Verified Daikin API capabilities.
2. OAuth2 implementation status.
3. Required Developer Portal setup.
4. Device discovery status.
5. Available telemetry fields.
6. Implemented manual control infrastructure.
7. Security measures.
8. Test results.
9. Remaining blockers.
10. Exact steps for connecting the real ONECTA account.

Commit all completed changes with a descriptive message.

Do not push to GitHub automatically.

Prioritize secure and correct integration behavior over feature completeness.

If external credentials or provider approval are unavailable, implement the safe infrastructure and document the required next steps without inventing successful live connectivity.

## Execution summary

- Verified Daikin's 200-call sliding-day quota and unauthenticated rejection by the developer catalog and gateway endpoint. Local ONECTA developer credentials were absent, so OAuth exchange, discovery, readings and command contracts were exercised with fixtures.
- Added browser-bound authorization-code flow, authenticated token encryption, explicit scoped assignments, independent quota-limited polling, telemetry persistence, and the Daikin dashboard. Manual commands are feature-flagged off by default and use a durable audit and observed-state verification.
- Release build completed without warnings. All 37 backend and seven frontend tests passed, and the frontend production build passed. Isolated backend smoke checks returned HTTP 200 for health, HTTP 401 for protected API routes, and HTTP 400 for an invalid OAuth callback.
- MySQL 8.0.46 was checked without changing existing data. The runtime account lacks the DDL privilege needed for migration 003, and the migration is pending. Live provider sign-in and readings remain unverified. No control command was sent to real equipment.

See the [ONECTA integration guide](../integrations/daikin-onecta.md), [migration 003](../../database/migrations/003_daikin_onecta.sql), and [React view](../../src/frontend/home-automation-web/src/features/DaikinPanel.tsx).
