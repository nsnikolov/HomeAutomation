# Investigate Milestone 4H live electrical telemetry

Date: 2026-11-23

## Original prompt

Investigate Milestone 4H live electrical telemetry.



Confirmed:

- Ewelink:EnableWebSocket=true
- Ewelink:EnableLiveElectricalReporting=true
- All five PSF-X67 devices show "Electrical reporting accepted. Waiting for a measurement change."
- Official eWeLink reports approximately 500 W for "Inverter out".
- HomeAutomation still shows 241.44 W, last known at 16:25.
- The power difference is well above the documented reporting threshold.

Trace the complete path:\
CoolKit WebSocket -> incoming message -> device mapping -> telemetry persistence -> SignalR -> React.



Check whether:

1. CoolKit sends electrical events after uiActive acceptance.
2. Incoming message formats are correctly parsed for UIID 32.
3. Events are filtered or discarded incorrectly.
4. Device identifiers map to the correct internal devices.
5. Timestamps or deduplication incorrectly reject valid readings.
6. SignalR delivers persisted updates to the correct property.

Add temporary, opt-in, sanitized diagnostics showing counts and rejection reasons at each stage. Never log tokens or full sensitive payloads.



Preserve existing relay safety, API limits and HTTP polling.



Run tests and builds. Do not send live relay commands. Archive the prompt and commit locally without pushing.

## Execution summary

A read-only audit of the local Home Automation schema found five owned UIID 32 meters, all assigned and correctly bound. It found zero socket power snapshots and no persisted eWeLink electrical series. The observed gap was therefore upstream of SignalR and React. The running backend predated the new trace, so the live distinction between missing provider reports and rejecting incoming frames remains unresolved.

Fixed a reproducible ordering defect where valid electrical events arriving near HTTP synchronization could be rejected against an undated snapshot's receipt time. Electrical push readings now use a per-field provider timestamp watermark and remain preferred while fresh. Relay ordering continues to use its existing checks.

Added opt-in counts for acknowledgements, wire shapes and rejection reasons, inventory matching, history and snapshot writes, property-scoped SignalR sends, browser receives and refreshed cards. Both builds passed. Backend tests passed 144 with seven opt-in MySQL tests skipped. Frontend tests passed 31. The local audit executed only SELECT queries. No live relay commands were sent.

- [Temporary trace setup and interpretation](../integrations/ewelink-cloud.md#trace-electrical-reports)
- [Electrical timestamp arbitration](../../src/backend/HomeAutomation.Infrastructure/Ewelink/EwelinkMapping.cs)
- [Sanitized trace counters](../../src/backend/HomeAutomation.Infrastructure/Ewelink/EwelinkSocketTrace.cs)
