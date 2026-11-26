# Continuous eWeLink electrical collection

Milestone 7D lets the backend collect power, voltage and current history from eWeLink power-detection devices with no browser open. It reuses the existing account WebSocket, activation command, ingestion path and request budget. It adds no migration, no worker and no new storage. Collection is **off by default**.

## Root cause of the live-view dependency

These facts are verified in the repository and in CoolKit's published protocol.

1. **HTTP inventory values are not history.** The five-minute inventory poll returns `power`, `voltage` and `current` for UIID 32 devices. These are the cloud's last-held values and carry no measurement time. They correctly update only the card snapshot. HTTP synchronization writes relay state and reachability, not electrical samples.
2. **History comes only from WebSocket device reports.** [CoolKit's UIID 5 protocol](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/UIIDProtocol.md), which UIID 32 inherits, has two rules:
   - The device reports electrical values only during a `uiActive` activation window, which lasts 60 seconds by default, and does not report outside it.
   - Inside the window it reports only when power changes by more than 2 W, voltage by more than 5 V, or current by more than 0.03 A.
3. **Before 7D, only a browser triggered activation.** `uiActive` was sent only to properties with a live viewing lease from an open, visible eWeLink page (`EwelinkElectricalReporting.TryStart` required a viewer). With no browser, no activation was sent, the meters stayed silent, and no electrical samples were stored.

Other hypotheses were ruled out:
- Capability mapping, timestamp handling and SignalR were not the cause: frames received during activation were persisted with or without a connected client.
- One de-duplication rule did lose data, and 7D fixes it. It dropped a receipt-timed field whose value equalled the previous receipt-timed value, even inside a genuinely new report. This affected, for example, an unchanged voltage when power changed.

## Options considered

| Option | Result |
| --- | --- |
| A. Passive WebSocket stream | Insufficient. The protocol documents no reports outside activation windows. |
| B. Read-only refresh: HTTP `thing/status` or the WebSocket `query` action | Rejected as a sample source. Both return the cloud-held parameters, with no time or evidence that the value is current. Storing them on a timer would turn a stale value into a "new" observation. |
| B′. Background activation windows over the existing socket | **Chosen.** Uses the documented `uiActive` parameter, which only opens a reporting window. Every sample is a device-originated report. |
| C. Energy counters (`oneKwh`, `hundredDaysKwh`) | Out of scope. `oneKwh` is a start/stop/get command that changes device state, and `hundredDaysKwh` is a separate device request. No energy counter is invented, so eWeLink energy remains estimated from power. |

## Mechanism

- **One loop per account socket.** `EwelinkSocketWorker` remains the only worker. After `userOnline` authentication, its socket session runs `EwelinkElectricalCollector`, the activation loop that previously lived in the worker. This loop serves operator viewing leases and the background schedule together.
- **Schedule.**
  - Each eligible meter gets one 60-second `{"uiActive":60}` window per effective interval.
  - Viewer windows renew every 50 seconds, as before.
  - A viewer window also satisfies the schedule, so the next background window comes one interval after the last viewer window.
  - Many tabs or users share one activation per device. Provider traffic does not grow with browsers.
- **Restart.**
  - Each newly seen meter waits 60 seconds plus a stable per-device offset of up to five minutes. A restart never fires every meter at once, and the spread is the same on each start.
  - At most eight windows are open at once.
  - The schedule is in memory. Collection resumes automatically after a restart or reconnect, and the downtime stays a gap in history.
- **Serialization.** Commands are sent one at a time and each waits up to four seconds for its acknowledgement. The integration gate is taken without waiting, so HTTP synchronization and relay commands are never blocked. Other providers are unaffected.
- **Persistence.** Frames follow the existing path: `IngestSocketEventAsync`, then `TelemetryWriter.StoreCloudDeviceAsync`, then the card snapshot and a property-scoped SignalR invalidation. A failed SignalR notification is now logged and no longer ends the socket session, because the sample is already stored.

## Eligibility

A device receives activations and produces electrical samples only when all of these hold:
- It is owned, not shared.
- It reports UIID 5 or UIID 32 (the documented power-detection protocol, for example PSF-X67 and Sonoff POW-class plugs). The model string is not part of the contract.
- It is assigned to a property the linked account can read. Unassigned devices and other properties are skipped and counted.
- HTTP synchronization reports it online within `PollMinutes + 2` minutes. Offline or stale devices are skipped until they return.

Thermostats and other UIIDs never generate power observations. UIID 190 (POWR3-class) and other protocols are not mapped. Relay writes keep their separately verified, narrower PSF-X67 contract.

## Request budget

CoolKit's free APPID allows 50,000 requests per region per month and 300 per five minutes per IP. CoolKit does not say whether WebSocket updates count toward the quota, so every activation is charged to the shared regional budget, as before.
- **Rolling ceiling (unchanged).** Electrical activations stop once the sum of today's bucket and the 31 before it reaches 45,000.
- **New daily share.** Electrical activations also stop until the next UTC midnight once the day's bucket reaches 45,000 / 32 = **1,406** requests. This also caps viewer renewals, which could previously spend the monthly budget in hours.
- **Effective interval.** The configured interval is stretched, never shortened, so that eligible meters × activations per day fit the electrical allowance. The allowance is 1,406 minus an estimated three HTTP calls per inventory scan: 1,406 − 864 = **542 per day** at the default five-minute poll.

Expected electrical requests at the default five-minute HTTP poll (HTTP uses about 864 requests per day; 32-day totals stay at or below 45,000):

| Eligible meters | Configured 900 s (default) | Configured 300 s |
| --- | --- | --- |
| 5 | 900 s → 480/day | stretched to 797 s → 542/day |
| 10 | stretched to 1,594 s (≈27 min) → 542/day | 1,594 s → 542/day |
| 20 | stretched to 3,188 s (≈53 min) → 542/day | 3,188 s → 542/day |
| 50 | stretched to 7,970 s (≈2.2 h) → 542/day | 7,970 s → 542/day |

A five-minute window per meter fits the free quota only for one or two meters. A paid APPID would change these limits, but the quota is not configurable in this milestone.

## Retries and failures

| Event | Behavior |
| --- | --- |
| Acknowledgement `0` | Window open. Back-off and the account-wide unacknowledged streak reset. |
| CoolKit `504` ("the device does not respond") | Only that meter backs off: 1, 2, 4 minutes and so on, up to 6 h, with up to 10 % deterministic jitter. It retries when the back-off ends. Other meters continue. The card shows **The device did not respond**. |
| No acknowledgement within 4 s | With background collection, that meter backs off. Three in a row across meters end the socket session, and the worker reconnects with its existing 15 s–15 min back-off. In viewer-only mode, activation stops for the process, as before. |
| Any other CoolKit code | Activation stops for the backend process, as before, and the card shows the code. Review the code before restarting. |
| `quotaExceeded` or `syncDeferred` | No sends until the returned retry time (next UTC midnight for the daily share). |
| Socket closed, network error, rejected handshake, expired access token | Unchanged: the session ends and the worker reconnects with bounded back-off after HTTP synchronization refreshes the grant. Activation state survives reconnects. |
| Malformed or unrecognized frames | Ignored and counted in the optional trace, as before. |

## Timestamps, duplicates and freshness

- **Timestamps.** A provider device sequence or envelope time is used when present and recent. CoolKit sends UIID 32 electrical frames without one, so these use the UTC receipt time and are stored with `timestamp_source = 'serverReceived'`. A fresh provider-timestamped value still takes precedence. Per-field watermarks reject out-of-order and duplicate times. Invalid, NaN, infinite or out-of-range values are dropped; missing values never become zero.
- **Current versus repeated.**
  - Every accepted sample comes from a device-originated frame received by the backend. A timer never writes a value.
  - A receipt-timed frame is treated as a **repeated delivery** only when every field repeats the previous receipt-timed values within one 60-second activation window. Fields superseded by a fresh provider-timestamped value are left out of that comparison. Inside a window the device reports only threshold changes, so an identical frame there is a duplicate.
  - Otherwise the frame is a current report: all of its fields are kept, including an unchanged voltage. A report in a later window with the same value as before is also kept as a current reading.
  - `telemetry_samples` stays idempotent on series and observation time.
- **Freshness and history hold.**
  - The live **Live** label and the 7A maximum hold both stay at 2 minutes for eWeLink electrical series.
  - The hold was not extended to the collection interval. Between windows the device sends nothing, so a longer hold would invent coverage.
  - With one report per 15-minute window, an hour shows at most about 13 % coverage from those reports, plus any change reports. A steady load may produce no report at all if the device sends none at activation; this is unverified.
  - Estimated energy is therefore normally **partial**: below the 80 % threshold, integrated only over held intervals, never across gaps, and with no zero inferred for an offline device.

## 7A, 7B and 7C integration

- **7A.** Samples reach the existing raw store, so the rollup worker and backfill aggregate them like any other series. Measured and estimated energy stay separate, and the existing coverage and quality rules apply.
- **7B.** Logical history uses only observations inside each assignment. Samples keep their physical device and `main` endpoint, and the `ewelink` source key is unchanged, so no additional source or precedence rule exists. Replacements are unaffected.
- **7C.** History charts gain real samples automatically and keep gaps and low coverage visible. Nothing in the frontend activates collection.

## Configuration

| Setting | Default | Meaning |
| --- | --- | --- |
| `Ewelink:ContinuousElectricalCollection` | `false` | Background activation windows. Requires `Ewelink:EnableWebSocket=true`. Independent of `EnableLiveElectricalReporting`, which still controls operator viewing. |
| `Ewelink:ElectricalCollectionSeconds` | `900` | Requested spacing per meter, from 300 to 3,600. The budget can stretch it. An out-of-range value disables collection with a startup warning, and a vault import rejects it. |

Both settings travel in the provider vault snapshot as optional operational fields. A profile imported before 7D loads with collection off. Settings apply on restart. Credentials, OAuth grants, the Data Protection key ring and vault encryption are unchanged.

## Diagnostics

All diagnostics are aggregate: no device IDs, names, measurement values, tokens or payloads.

- **At worker start:** `eWeLink electrical collection mode {Mode}`, either `backgroundWindows`, `viewerOnly` or `passive`, with the requested interval.
- **Every 15 minutes while collecting:** `eWeLink electrical collection summary`, which reports:
  - the number of eligible meters, and how many had a persisted observation in the period;
  - the effective interval, and seconds since the last accepted activation;
  - the age of each meter's last persisted observation, oldest last, with `none` for meters with no observation yet;
  - counters: `activationSent`, `activationAccepted`, `deviceUnavailable`, `activationUnconfirmed`, `deferred.*`, `skipped.offlineOrStale`, `skipped.propertyScope`, `electricalFramesPersisted`, `electricalSamplesPersisted` and `failure.signalRSend`.
- **On events:** warnings for throttling and deferral, stops, and reconnects after unacknowledged commands.
- **Optional trace:** the existing `Ewelink:WebSocketDiagnostics` trace still reports per-frame rejection reasons.

## Verified and unverified

- **Verified with fixtures and disposable MySQL 8.0.46:**
  - scheduling, stagger, back-off, budget stretching and the daily guard;
  - the collector loop under acceptance, 504, rejection, throttling, lost acknowledgements and cancellation;
  - de-duplication, vault compatibility and end-to-end persistence;
  - honest rollup coverage, with no viewer and only `uiActive` sent.
- **Not verified on real hardware:**
  - whether a meter sends a report when a window opens, or only on later changes;
  - whether CoolKit counts WebSocket updates against the quota;
  - whether other UIID 5/32 models accept `uiActive` like the PSF-X67;
  - the long-run acknowledgement rate.

  Live acceptance of `uiActive` and receipt of timestamp-less frames were previously observed on the installed PSF-X67 meters.
