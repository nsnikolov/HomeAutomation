# Fix eWeLink WebSocket Electrical Timestamp Handling

Date: 2026-11-23

## Original prompt

# Fix eWeLink WebSocket Electrical Timestamp Handling

The live diagnostics identified the exact issue.



CoolKit successfully sends UIID 32 electrical reports:

- activationAccepted=5
- electricalFrames > 0
- power, voltage and current are present as strings
- envelope.ts is missing
- all electrical reports are rejected with timestampMissingOrInvalid

Fix this without changing the existing WebSocket activation or relay controls.



Requirements:

1. Accept authenticated, correctly identified CoolKit device-originated electrical frames when envelope.ts is absent.
2. If a valid provider timestamp exists, continue using it.
3. If no provider timestamp is available, use the backend's UTC receive time as an observation timestamp, explicitly marking its provenance as server-received rather than device-reported.
4. Preserve per-field timestamp ordering and deduplication. A timestamp-less frame must not overwrite a newer verified provider-timestamped reading or cause out-of-order regression.
5. Validate device identity, UIID, property ownership, field types and numeric ranges before persistence.
6. Persist accepted electrical samples to MySQL and publish updates through existing SignalR.
7. Ensure the React cards show fresh readings and distinguish their timestamp provenance where useful.
8. Add regression tests using the actual diagnosed frame shape:
   - userAgent=device
   - valid apikey
   - params.power, params.voltage, params.current as strings
   - missing envelope.ts
   - missing d_seq, sequence and from
9. Keep sanitized diagnostics with counters for accepted, persisted, broadcast and rejected reports.
10. Preserve relay-write safety, quotas, polling and existing functionality.

Run builds and tests, archive the prompt and commit locally. Do not push or send physical device commands.

## Execution summary

Accepted authenticated, timestamp-less UIID 32 electrical updates using the UTC time the complete message reached the backend. The fallback targets assigned, owned PSF-X67 devices and applies only to electrical fields. Valid provider timestamps retain priority, per-field watermarks reject regressions, and unchanged receipt-timed values are deduplicated. Cards label receipt-time readings as server received and continue updating from the existing property-scoped SignalR notification.

Migration 010 adds explicit sample provenance in MySQL. The migration runner succeeded twice on disposable MySQL 8.0.46, and an isolated regression persisted all three electrical measurements from a fixture frame with no timestamp and confirmed the relay state was untouched. The live schema was left unchanged and must have migration 010 applied to enable the fallback. No live device commands were sent.

Both builds passed. The standard suite passed 147 backend tests and skipped seven opt-in database tests. The dedicated eWeLink provenance test passed separately against disposable MySQL. All 31 frontend tests passed.

- [Migration and trace setup](../integrations/ewelink-cloud.md#passive-websocket-telemetry)
- [Per-field ordering and provenance](../../src/backend/HomeAutomation.Infrastructure/Ewelink/EwelinkMapping.cs)
- [Migration 010](../../database/migrations/010_ewelink_receive_time_provenance.sql)
