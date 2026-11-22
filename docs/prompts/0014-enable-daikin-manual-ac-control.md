# Milestone 3E — Enable Daikin ONECTA Manual AC Control

Date: 2026-11-22

## Original prompt

# Milestone 3E — Enable Daikin ONECTA Manual AC Control

Act as a Senior .NET 10 Engineer, IoT Integration Specialist, and React Engineer.

Complete and safely enable the existing Daikin ONECTA manual control functionality for supported air conditioners.

The user explicitly wants manual control of air conditioners from Home Automation, including:

- Power ON/OFF
- Target temperature changes
- Heating/Cooling mode selection
- Fan speed where supported

Prioritize power and target temperature.

The user also wants to minimize ONECTA API consumption by combining commands where supported and eliminating unnecessary requests.

## 1. Existing Project

Repository: C:\Git\HomeAutomation

Technology:
- .NET 10
- React + TypeScript
- MySQL / Dapper
- Daikin ONECTA Cloud API
- OAuth2 with PKCE
- Existing provider-neutral capability model
- Existing command infrastructure and audit trail

Review AGENTS.md, relevant Skills, architecture documents, and Daikin integration documentation.

Recent milestone:

Milestone 3D, commit `88065a9`.

Do not rebuild the existing integration.

Preserve all working Growatt, Daikin, and simulator functionality.

Review any existing uncommitted changes before starting.

## 2. Current Control Limitation

Manual commands are implemented but physical execution remains disabled.

A major blocker was that real ONECTA air conditioner responses lacked `lastUpdateReceived`.

The available gateway timestamp has not been confirmed as a device-observation timestamp.

Therefore, the current safety validation may reject physical commands because it cannot verify device state freshness.

Resolve this safely.

## 3. Freshness Validation

Investigate the existing command freshness checks.

Determine whether a successful authenticated read-only ONECTA GET can provide a sufficiently recent provider response for a supervised manual command.

Distinguish:

- Provider observation timestamp
- Gateway timestamp
- Backend successful receipt time
- Cached database timestamp
- Device connectivity state

Do not represent backend receipt time as device measurement time.

Where the API does not provide a trustworthy device observation timestamp, implement a clearly documented command-specific freshness policy based on a recent successful provider read, advertised reachability and capability validation.

Fail closed when any required safety condition cannot be established.

Do not globally weaken the existing freshness rules.

Maintain stronger restrictions for automation and non-interactive commands.

## 4. Enable Manual AC Control

Reuse the existing command handlers.

Support:

- Power ON/OFF
- Target temperature
- Heating/Cooling mode
- Fixed fan speed where supported

Validate every command against the device's currently advertised capabilities.

Enforce:

- Property membership and operator authorization
- Supported mode and characteristic
- Allowed temperature range
- Temperature increment
- Provider reachability
- Request freshness
- Pending/uncertain command blocking
- API quota availability
- Command audit logging
- Provider-state confirmation

Do not assume an HTTP 2xx means the physical device has applied the command.

Preserve the last confirmed reported state until verification succeeds.

## 5. Explicit Exclusion — Altherma

The Daikin Altherma device named `Heatpump` must remain read-only.

Block Altherma physical commands at both:

- Application command handler
- ONECTA HTTP adapter

Do not permit changing:

- Heat pump power
- Leaving-water target
- Heating mode
- Emergency settings
- Any Altherma configuration

Do not permit general write flags to bypass these restrictions.

## 6. Optimize ONECTA API Usage

The private ONECTA API has a published limit of 200 requests in a rolling 24-hour window.

Investigate the official authenticated API contract and existing adapter.

Determine whether a single supported API request can update multiple characteristics or multiple devices.

Do not assume batch updates are supported.

If an officially documented batch operation exists, implement it with correct validation and error handling.

Otherwise:

- Use one PATCH per necessary provider operation.
- Skip operations whose requested values already match confirmed state.
- Coalesce rapid temperature adjustments into one final command.
- Avoid sending commands for every +/- button click.
- Debounce UI changes where appropriate.
- Prefer an explicit Apply action for target temperature if safer.
- Do not automatically retry ambiguous PATCH operations.
- Avoid unnecessary extra GET requests.
- Reuse a valid recent provider snapshot where safe.
- Batch confirmation reads across devices using the existing GET endpoint where supported.
- Preserve enough quota for background monitoring.

Do not delay safety-critical manual OFF actions merely to coalesce commands.

Track the number of API requests consumed by each manual operation.

Clearly document expected API calls for:

- Power ON/OFF
- Temperature change
- Mode change
- Multiple changes to one device
- Multiple device changes

## 7. Physical Write Configuration

Preserve the existing `Daikin:EnableWrites` feature flag.

Do not permanently enable writes as a side effect of deployment.

Implement the code and validate it using mocked ONECTA responses first.

The user has authorized developing the manual control feature, but has not yet approved an actual physical command on a specific device.

Do not send real commands during implementation or automated tests.

Prepare exact instructions for enabling writes in a supervised session.

When the operator explicitly approves a device and one action, perform only that action.

Provide a straightforward way to disable writes afterward.

## 8. React Interface

Update the existing Daikin ONECTA device cards.

For eligible AC devices, provide:

- Power ON/OFF
- Temperature controls
- Heating/Cooling mode
- Fan speed if supported
- A clear Apply action where appropriate

Display:

- Current reported state
- Requested changes
- Pending operation
- Confirmed success
- Failed operation
- Uncertain operation
- Last successful synchronization
- Write enable/disable status

Avoid duplicate commands from double-clicks or repeated UI events.

Do not optimistically show an unverified setting as the actual device state.

Altherma must remain read-only.

## 9. Controlled Manual Verification

Once automated tests pass, prepare a supervised verification procedure.

Recommended test device:

Sturgel Kitchen

Example action:

Change target temperature from the currently reported value to one nearby supported value.

Do not hardcode 24°C or 23°C.

Before testing:

- Confirm the device is assigned to the correct property.
- Confirm advertised writable capabilities.
- Confirm device reachability and command freshness.
- Confirm available API quota.
- Require explicit approval for the exact physical action.

After testing:

- Observe resulting device state through ONECTA.
- Verify the audit record.
- Check actual API request consumption.
- Restore read-only configuration.

If user confirmation is unavailable, stop before sending any physical command.

## 10. Tests

Add or update tests for:

- Valid manual commands
- Fresh read-based command authorization
- Missing/stale reachability
- Unsupported settings
- Temperature min/max/step
- Duplicate-command elimination
- Debounced temperature changes
- Command coalescing
- Quota accounting
- Ambiguous PATCH outcomes
- Command verification
- Property authorization
- Altherma write rejection
- Disabled physical-write flag

Run:

- .NET Release build
- Backend tests
- Frontend production build
- Frontend tests
- Relevant database checks

Never execute real device commands as part of tests.

## 11. Documentation

Update:

- docs/integrations/daikin-onecta.md
- Relevant command and API quota documentation
- Relevant architecture documentation, if needed

Archive this complete prompt under docs/prompts/ using the next sequential filename.

Update the prompt index.

## 12. Final Report

Provide:

1. Whether manual AC control is technically ready.
2. Exact freshness validation approach.
3. Supported controls.
4. Which devices are eligible.
5. API batching findings.
6. Request-saving optimizations.
7. API calls expected per command.
8. Physical write flag status.
9. Test results.
10. Exact instructions for a supervised live command.

Commit verified implementation changes locally.

Do not push automatically.

Do not execute physical commands without explicit approval.

Prioritize correct, quota-efficient manual operation over additional features.

## Execution summary

Manual AC requests can reuse an authenticated ONECTA response for two minutes or fetch one account-wide preflight when necessary. Connectivity and advertised capabilities remain required. Provider observation time, gateway metadata, and response receipt stay distinct. Temperature edits use a single Apply action. Accepted PATCHes stay pending until matching readback, ambiguous PATCHes are not replayed, and the existing budget preserves background read capacity. The running backend has writes explicitly disabled. Altherma remains read-only.

Release and frontend production builds passed. All 55 backend tests and nine frontend tests passed. One authenticated discovery found four assigned online ACs. Two advertised Celsius temperature controls. Sturgel Kitchen supplied a writable range and step with no unit. The normal startup poll stored separate receipt timestamps, HTTPS readiness passed, and the live command audit remained empty. Live physical verification awaits approval for one exact device action.

- [ONECTA manual AC and quota guide](../integrations/daikin-onecta.md#manual-ac-freshness-and-readback)
- [Command handler](../../src/backend/HomeAutomation.Infrastructure/Daikin/DaikinService.cs)
- [Daikin device cards](../../src/frontend/home-automation-web/src/features/DaikinPanel.tsx)
