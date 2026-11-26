# Logical devices and physical replacement

Milestone 7B separates the stable identity of a monitored role, such as "Heat pump power meter", from the replaceable hardware that measures it. Physical devices, telemetry series, raw samples and hourly rollups are unchanged. A logical device only records which physical device served it and when.

## Model

Migration `018_logical_devices.sql` adds three tables. Every row is scoped to one property through composite foreign keys, so a logical device can only reference devices, rooms and endpoints of its own property. Milestone 7E adds management, migration 019 and automatic provisioning; see [logical device management](14-logical-device-management.md).

| Table | Purpose |
| --- | --- |
| `logical_devices` | Name, kind, optional room and description, created and updated times, archive time. `origin_device_id` records the physical device a logical device was initialized from; its unique key makes initialization idempotent. |
| `logical_device_assignments` | One row per assignment of a logical **channel** to a physical device, optionally pinned to one endpoint, valid for `[valid_from_utc, valid_to_utc)`. Records who assigned it, when, and why. Rows are closed, never deleted. |
| `logical_device_audit` | Append-only record of `created`, `initialized`, `updated`, `assigned`, `replaced`, `corrected` and `archived`, with actor, operation time, old and new device, effective time and reason. No provider data or secrets. |

### Channels, devices and endpoints

A channel is the exclusive unit of replacement. Most logical devices have one channel, `main`. A channel is assigned to a **physical device**, and each canonical `Metric` is resolved across that device's endpoints. This is the simplest mapping that works across providers: a Tuya thermometer reports temperature and humidity on separate data-point endpoints, while an eWeLink THR320D reports both on one endpoint. Replacing one with the other keeps temperature continuous without any endpoint mapping.

An endpoint is pinned only when a device reports the same measurement capability on more than one endpoint, such as a multi-gang relay. Initialization then creates one channel per endpoint, and an assignment to such a device must name the endpoint.

### Invariants

- Intervals are half-open. A reading exactly at the replacement instant belongs to the new device only.
- The generated `open_slot` unique key allows one open assignment per logical channel.
- A physical device, or one of its endpoints, cannot be assigned to two logical channels for overlapping time. A whole-device assignment conflicts with any endpoint of the same device. MySQL has no exclusion constraint, so this and channel overlap are validated in the application.
- Every change runs in one transaction that first locks the property row with `SELECT … FOR UPDATE`, so changes to one property are serialized. A replacement also names the assignment it expects to close, so a concurrent or stale request gets 409 instead of overwriting.
- Replacement times are stored to the microsecond and never rounded. They must be after the current assignment started and not in the future (two minutes of clock skew allowed). Server UTC is the default.
- Owner and Admin may change logical devices. Any property member may read them and their history.
- Archiving closes open assignments and keeps all history readable. Archived devices are read-only.
- A correction of a replacement time is a separate, audited operation that requires a reason and moves the boundary between two adjacent assignments.

## Compatibility

Compatibility compares canonical capability codes of the current and replacement source, ignoring status capabilities (`FaultStatus`, `WarningStatus`, `EmergencyStatus`).

| Case | Result |
| --- | --- |
| No measurement capability in common | Rejected as incompatible. |
| Replacement lacks a current capability | Rejected unless `acceptMissingCapabilities` is set. History of that metric stays readable; new readings are absent. |
| Replacement adds capabilities | Allowed. The new metric starts at the replacement time; nothing earlier is fabricated. |
| Measurement repeated on several endpoints | The endpoint must be chosen. |

## Continuous history

`GET /api/v1/properties/{id}/logical-devices/{logicalId}/history?metric=&channel=&resolution=raw|hour|day` returns one series for the channel. It uses the 7A range rules (31 days for raw and hourly, 366 calendar days for daily).

1. Each assignment becomes a segment. The physical series for the metric is the preferred stream of the 7A source rule (`tuya-sensor-report` before `tuya`, then the oldest series); the others are listed as alternatives. A segment is `unsupported` when the device does not report the metric and `ambiguous` when several endpoints do.
2. **Only observations inside a segment belong to the logical device.** A reading of the new device before its start, or of the old device after its end, is never counted or held across the boundary. Counter deltas therefore never cross devices: the first reading of the new device is a new baseline.
3. Hourly 7A rollups are reused only where their lookback and lookahead lie inside the segment (hold for gauges, 26 hours for counters). The edges, partial hours and replacement hours are calculated from filtered raw samples with the same 7A calculator. A 14:37 replacement therefore splits the 14:00 hour exactly.
4. Segments combine like 7A parts: coverage is summed, averages are weighted by covered seconds, extremes include every contributing value, and measured counter energy and estimated power energy stay in separate fields.
5. Buckets list their contributing `seriesIds` and add flags: `deviceReplaced` when several assignments touch the bucket, `unassigned` when part of it has no assignment, and `notReported` when the active device does not report the metric. Time that 7A has not aggregated stays `notAggregated`, never zero.

Raw logical history pages across segments in time order and tags each point with its series and assignment.

## Dashboard continuity

Logical dashboard sources use the key `logical:{logicalId}:{channel}:{Metric}` and provider `logical`.

- The source resolves to the active assignment's physical source for that metric. Physical sources are linked to core devices through `provider_bindings` (Daikin and eWeLink use their single endpoint; Tuya matches the data-point endpoint).
- A reading is shown only when the active device observed it at or after its assignment started. A retired device's reading, or a reading taken before the replacement, is never shown as current.
- Metrics that any assignment of the channel reported remain available as sources. When the current device does not report one, the source has status `unsupported` and the widget explains it. `unassigned`, `ambiguous` and `archived` are explained the same way.
- Existing physical bindings keep working unchanged. `GET …/dashboard/logical-bindings` proposes a conversion only where exactly one active logical channel uses the saved physical source, and lists every other value with a reason. `POST` converts only the values the editor selects, against the current layout revision, and updates a matching widget navigation target. Archived logical sources keep rendering but are not offered for new bindings.

## Initialization

`--initialize-logical-devices --logical-property=<id>` plans one logical device per physical device that has none, named after the device, in the same room and kind. The plan is read-only unless `--execute` is given, and all planned devices are then created in one transaction under the property lock. Assignments start at `1970-01-01T00:00:00Z`, so every recorded observation of a device, including devices with no samples yet, stays attributed to it. No past replacement is inferred. A device already assigned manually is skipped, and rerunning skips devices already initialized.

## Provider boundary

A replacement changes only these tables. It sends nothing to any device or cloud and changes no credentials, OAuth grants, vault profiles, polling, discovery, relay state or control permissions.

**Growatt hardware.** `TelemetryWriter.StoreAsync` refuses to ingest when stored Growatt bindings differ from the configured master and slave serials. A new inverter therefore cannot appear as a physical device until the configuration and bindings are changed in a separate, approved workflow, and 7B does not weaken that guard. The installation-level Growatt series are not part of a logical device. Until that workflow exists, a Growatt inverter replacement is out of scope; replacing other devices is unaffected.

## Deferred

- Creating logical devices and channels from the UI (the API supports it), endpoint-level channel editing and the correction UI.
- Daily and hourly logical charts (Milestone 7C).
- Automatic logical identities for devices discovered after initialization; rerun the initialization command or create them through the API.
- The Growatt hardware replacement workflow described above.
