# Milestone 4G — Real-Time eWeLink Telemetry via WebSocket

Date: 2026-11-23

## Original prompt

# Milestone 4G — Real-Time eWeLink Telemetry via WebSocket

Continue the existing HomeAutomation repository.



Problem:\
eWeLink PSF-X67 power, voltage and current readings are currently refreshed through HTTP polling every 5 minutes. The official eWeLink app updates readings automatically.



Goal:\
Implement near-real-time telemetry updates using the official CoolKit WebSocket API and existing SignalR infrastructure.

## 1. CoolKit WebSocket integration

- Review official CoolKit API V2 WebSocket documentation.
- Verify compatibility with our existing OAuth2 credentials and APPID.
- Implement a backend hosted WebSocket client with authenticated connection, handshake, heartbeat, reconnect and exponential backoff.
- Maintain one connection per authorized eWeLink account, not one per device.
- Handle connection loss, OAuth token refresh, cancellation and backend shutdown.
- Never log tokens, private API keys or full sensitive payloads.
- Do not use undocumented private interfaces.

## 2. Telemetry events

- Subscribe to relevant device status updates.
- Support verified UIID 32 PSF-X67 fields: relay state, active power, voltage and current.
- Support THR320D temperature, humidity and relay state where provided.
- Handle partial event payloads: update only fields actually present.
- Never overwrite valid measurements with missing fields.
- Validate units, timestamps, device identity and property scope.
- Do not assume every device publishes every measurement through WebSocket.
- Persist received observations using existing telemetry infrastructure.
- Preserve current command auditing and relay readback safeguards.

## 3. Live frontend updates

- Forward verified changes through the existing SignalR infrastructure.
- Update the matching React device card automatically without refreshing the page.
- Display the timestamp and freshness of readings.
- Clearly distinguish live readings from last-known values.
- Avoid unnecessary re-renders.

## 4. Fallback and safety

- Keep existing 5-minute HTTP polling for reconciliation and recovery.
- Do not increase HTTP polling frequency.
- Deduplicate repeated telemetry observations.
- Handle out-of-order events without replacing newer readings.
- Respect CoolKit quotas and connection limits.
- Keep the existing Force Refresh capability.
- WebSocket must be read-only for this milestone; do not send device-control commands through it.
- Preserve all ON/OFF controls, permissions, confirmations and safety rules.

## 5. Verification

- Use mocked WebSocket messages for tests.
- Test authentication, reconnect, heartbeat, token expiration, partial updates, stale readings, duplicate events and SignalR delivery.
- Verify whether the PSF-X67 actually provides live power measurements through the documented protocol.
- If WebSocket access is unavailable for our APPID, report the limitation and preserve the existing polling behavior.
- Run backend and frontend builds and tests.
- Document setup and troubleshooting.
- Archive the prompt and commit locally.
- Do not push or issue live device commands.

Keep changes focused and consistent with the existing .NET 10, Dapper/MySQL and React/SignalR architecture.

## Execution summary

Implemented an opt-in, account-scoped CoolKit listener with OAuth dispatch, TLS, a documented handshake, heartbeat and bounded reconnects. Assigned PSF-X67 and THR320D reports with a trustworthy provider timestamp are merged field by field, persisted through the existing telemetry writer and announced to the corresponding property through SignalR. The UI shows freshness and observation time. Five-minute HTTP reconciliation, Force Refresh and the manual relay audit and readback flow remain available.

The existing APPID and OAuth grant passed the live handshake. The 70-second read-only probe window produced zero eligible PSF-X67 electrical reports and zero eligible THR320D temperature reports. The official UIID 32 protocol inherits activation-dependent electrical reporting from UIID 5. The listener provides passive reception, and no live device commands were issued. No database migration was required.

The backend Release and frontend production builds succeeded. Backend tests passed 138 with seven opt-in MySQL tests skipped. Frontend tests passed 30. Coverage includes mocked WebSocket frames and a property-scoped SignalR delivery test.

- [WebSocket worker](../../src/backend/HomeAutomation.Infrastructure/Ewelink/EwelinkSocketWorker.cs)
- [Telemetry mapping](../../src/backend/HomeAutomation.Infrastructure/Ewelink/EwelinkMapping.cs)
- [eWeLink setup and limitations](../integrations/ewelink-cloud.md#passive-websocket-telemetry)
