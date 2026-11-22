# Daikin ONECTA — Debounced Command Confirmation

Date: 2026-11-22

## Original prompt

# Daikin ONECTA — Debounced Command Confirmation

Improve the existing ONECTA manual command confirmation flow.



Context:

- Milestone 3E is committed as `0bc2546`.
- Physical Power On and temperature changes were successfully tested on Sturgel Big Room.
- Commands currently remain Pending until the next 15-minute polling cycle.
- Physical writes are enabled by the operator. Preserve the existing configuration.
- Altherma must remain strictly read-only.

## Required behavior

Implement a shared, debounced confirmation mechanism.

1. After a successfully dispatched PATCH, schedule a verification GET for 30 seconds after that command.
2. If another command is successfully dispatched before the timer expires, reset the timer to 30 seconds after the latest command.
3. Use a single account-wide ONECTA GET to confirm all pending commands wherever possible.
4. Do not issue one verification GET per command or per device.
5. Reuse the existing telemetry ingestion and command audit pipeline.
6. Keep regular polling at 15 minutes.
7. If regular polling occurs during the debounce window and provides sufficient confirmation, avoid the additional GET.

## Multiple commands

Support safe sequential manual commands without waiting for the scheduled verification GET after every action.

- Permit additional commands for different devices while another device has pending confirmation.
- For the same device, permit sequential commands targeting different independent capabilities only when the previous PATCH outcome is known and accepted.
- Never allow overlapping or concurrent PATCH requests for the same device.
- Never automatically retry a PATCH.
- Block commands targeting a capability with unresolved or uncertain state.
- If a PATCH outcome is ambiguous, preserve the existing safety block for that device.
- Keep all property authorization, freshness, capability and quota checks.
- Do not treat an HTTP 2xx response as confirmation of physical execution.

For example:

- 14:00:00 — Power On dispatched.
- 14:00:10 — Target temperature dispatched.
- 14:00:20 — Fan speed dispatched.
- 14:00:50 — One account-wide verification GET.

Only one additional GET should be needed if all commands are visible in that response.

## Command state

Maintain separate states for:

- Dispatched
- Awaiting confirmation
- Confirmed
- Failed
- Uncertain

If the verification GET confirms the requested values, mark the relevant commands Confirmed.



If a value does not match, do not automatically retry the PATCH. Preserve an appropriate pending or uncertain state and allow the next normal polling cycle to provide further evidence.

## API quota

- Respect the existing 200-call rolling 24-hour quota.
- Account for every PATCH and GET.
- Coalesce verification requests across devices.
- Do not start a verification GET when the quota is exhausted.
- Fall back to scheduled polling when verification cannot be performed.
- Avoid busy loops or repeated verification attempts.

## Testing

Add focused tests for:

- Debounce timer reset.
- Multiple commands to different devices.
- Sequential independent commands to one device.
- Duplicate verification prevention.
- Ambiguous PATCH blocking.
- Partial confirmation.
- Quota exhaustion.
- Interaction with normal polling.

Run focused tests and relevant builds.



Update the Daikin documentation and prompt history.



Commit verified changes locally but do not push.



Do not send real physical commands during implementation.



Keep this change narrowly scoped. Avoid unrelated refactoring or UI redesign.

## Execution summary

Added one account-wide confirmation attempt 30 seconds after the last accepted PATCH. Regular discovery cancels the extra read when it confirms the full scheduled batch. Partial, deferred, and failed reads leave unresolved audits for normal polling. Independent commands can proceed after acceptance, and ambiguous outcomes retain the device-wide block. The app reuses existing quota accounting, audit storage, and telemetry ingestion.

The Release and frontend production builds passed. All 67 backend and 11 frontend tests passed. Read-only MySQL audit queries passed. The running service and physical-write configuration were left in place during implementation, and no physical commands were sent.

- [Daikin integration guide](../integrations/daikin-onecta.md#debounced-readback)
- [Confirmation worker](../../src/backend/HomeAutomation.Infrastructure/Daikin/DaikinConfirmationWorker.cs)
- [Confirmation tests](../../tests/HomeAutomation.UnitTests/DaikinConfirmationTests.cs)
