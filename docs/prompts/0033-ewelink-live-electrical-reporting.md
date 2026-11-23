# Milestone 4H — eWeLink UIID 32 Live Electrical Reporting

Date: 2026-11-23

## Original prompt

# Milestone 4H — eWeLink UIID 32 Live Electrical Reporting

Continue HomeAutomation after commit 9557140 and subsequent changes.



Problem:\
The eWeLink WebSocket connection works, but PSF-X67 electrical measurements remain stale.



Live example:

- Official eWeLink Web: Boiler = 1908.02 W, 239.83 V, 7.97 A.
- HomeAutomation: Boiler = 0 W (last known), despite a recent cloud sync.

Root cause to investigate:\
Official CoolKit UIID protocol documents that UIID 32 inherits UIID 5 electrical reporting behavior.



The documented activation payload is:\
{"uiActive":60}



Activation lasts 60 seconds. The official app refreshes it approximately every 50 seconds while the relevant UI is active.



Reference:\
[https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/UIIDProtocol.md#uiid5-power-detection-single-channel-plug](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/UIIDProtocol.md#uiid5-power-detection-single-channel-plug)

## Implementation

1. Extend the existing authenticated CoolKit WebSocket client to support the documented uiActive activation command.
2. Activate reporting only for verified, online PSF-X67 devices (UIID 32).
3. Use a separate explicit configuration:\
   Ewelink:EnableLiveElectricalReporting = false
4. When the feature is enabled and an authorized operator is actively viewing the eWeLink page:
   - Send uiActive=60 through the documented WebSocket command protocol.
   - Renew approximately every 50 seconds while viewing.
   - Stop renewing when the page is no longer active.
   - Coordinate multiple browser sessions to avoid duplicate activation requests.
   - Bound the number of active devices and commands.
   - Respect provider rate limits, cooldowns and connection restrictions.
   - Never create a new WebSocket connection per device.
5. Important safety:
   - uiActive is telemetry activation only.
   - Never send switch, alarm, overload protection, startup or relay commands.
   - Do not modify existing per-device relay-write permissions.
   - Keep Inverter In and Inverter out relay controls protected.
   - Do not send live activation commands during implementation or automated tests.
   - If documented activation is rejected, stop and report the actual failure.
6. Telemetry:
   - Parse valid power, voltage and current reports.
   - Preserve partial-report handling and timestamps.
   - Persist observations and forward through existing SignalR.
   - Update cards automatically in seconds when reports arrive.
   - Do not display old measurements as current.
   - If activation succeeds but no electrical reports arrive, show that distinction in diagnostics.
   - Do not assume a report arrives without a sufficient measurement change.
7. Verification:
   - Mock activation, renewal, expiry, navigation away, concurrent sessions, reconnect and failed responses.
   - Test that activation never changes relay state.
   - Keep five-minute HTTP reconciliation unchanged.
   - Run backend/frontend builds and tests.
   - Update documentation and archive prompt.
   - Commit locally without pushing.

No schema migration unless strictly necessary.



Keep implementation focused.

## Execution summary

Implemented opt-in UIID 32 telemetry activation using the existing CoolKit account socket. Authorized visible operator tabs share a bounded viewing lease. The backend checks assignment, property access, account cooldown and meter freshness before sending the telemetry-only payload. It renews eligible meters at approximately 50 seconds, tracks CoolKit acknowledgement separately from measured values, and stops activation on rejection or acknowledgement timeout. Five-minute HTTP reconciliation and relay permissions continue to apply.

Both builds passed. Backend tests passed 142 with seven opt-in MySQL tests skipped. Frontend tests passed 31. Verification used mock activation and provider messages. Live acceptance on the installed meters remains unverified. No live activation commands were sent and no schema migration was required.

- [Operator setup and troubleshooting](../integrations/ewelink-cloud.md#operator-viewed-electrical-reporting)
- [Viewer coordination and provider eligibility](../../src/backend/HomeAutomation.Infrastructure/Ewelink/EwelinkElectricalReporting.cs)
- [Mocked WebSocket verification](../../tests/HomeAutomation.UnitTests/EwelinkSocketTests.cs)
