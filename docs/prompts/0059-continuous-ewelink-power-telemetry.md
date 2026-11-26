# HomeAutomation — Milestone 7D: Continuous eWeLink Power Telemetry

Date: 2026-11-26

## Original prompt

# HomeAutomation — Milestone 7D: Continuous eWeLink Power Telemetry

Implement Milestone 7D: reliable, continuous background collection and persistence of electrical power telemetry from supported eWeLink / Sonoff / Tongou devices.

The primary objective is to collect historical electrical measurements independently of any open browser, dashboard, monitoring page, or active SignalR client.

This milestone must integrate with the existing historical telemetry infrastructure introduced in Milestones 7A, 7B and 7C.

Use extended/high reasoning, particularly for provider protocol analysis, reliability, rate limiting, timestamps, and background processing.

## 0. Project context

Before making changes:

1. Read `CLAUDE.md`, `AGENTS.md` and `docs/AI_HANDOFF.md`.
2. Review recent Git commits and current Git status.
3. Inspect the existing eWeLink integration architecture.
4. Read the provider documentation and relevant ADRs.
5. Read:
   - `docs/architecture/10-historical-telemetry.md`
   - `docs/architecture/11-logical-devices.md`
6. Inspect telemetry ingestion, workers, WebSocket handling, device capabilities and historical data APIs.
7. Inspect frontend live-monitoring and SignalR subscriptions.

Relevant completed milestones:

- 7A — Historical Telemetry, commit `754615e`.
- 7B — Logical Devices, commit `35838fa`.
- 7C — Historical Charts, commit `33a2c5c`.

Migrations 017 and 018 may not yet be deployed.

Do not assume that the live database has been migrated.

Preserve the existing .NET 10, Dapper, MySQL, React, SignalR and Configuration Vault architecture.

Produce a concise implementation plan before editing code.

## 1. Main objective

The system currently provides monitoring of eWeLink devices, including Sonoff and Tongou power-monitoring relays.

However, the Milestone 7A analysis reported that electrical measurements may only be received or persisted while the corresponding live view is open.

This behavior must be investigated and corrected.

The desired behavior is:

- The backend continuously maintains electrical telemetry collection.
- Collection does not depend on browser sessions.
- Collection does not depend on the number of connected users.
- Data collection resumes automatically after backend restarts.
- Supported electrical measurements are persisted to existing telemetry storage.
- Milestone 7A aggregates them into hourly and daily history.
- Milestone 7B logical devices retain historical continuity.
- Milestone 7C displays actual collected history and coverage.

Do not introduce a second historical storage system.

## 2. Investigate the current implementation first

Trace the complete eWeLink data flow:

1. Provider authentication.
2. WebSocket connection establishment.
3. Device discovery and capability mapping.
4. WebSocket subscriptions, if any.
5. Incoming device messages.
6. Electrical parameter extraction.
7. TelemetryWriter persistence.
8. SignalR / live-monitoring updates.
9. Existing background polling.
10. Device detail requests triggered by the UI.

Determine why electrical data collection depends on the live view.

Possible causes to investigate include:

- Device details are fetched only when the UI requests them.
- Electrical parameters are not included in ordinary WebSocket notifications.
- A subscription or initialization operation is triggered only from an interactive endpoint.
- WebSocket events arrive only after device state changes.
- The background worker persists state changes but does not persist electrical readings.
- The provider only reports certain electrical fields through a separate read-only operation.
- Electrical telemetry is discarded because of incomplete capability mapping.
- Current timestamp or deduplication behavior prevents repeated samples from being persisted.

These are hypotheses, not established facts.

Identify the actual cause from the repository and available provider documentation before designing the solution.

Clearly distinguish verified behavior from assumptions.

## 3. Preferred architecture

Use the existing eWeLink integration and background worker infrastructure.

Prefer the following approach, in order:

### Option A — Existing WebSocket stream

If authenticated WebSocket messages provide the required electrical values reliably:

- Process them continuously.
- Parse electrical measurements centrally.
- Persist them through the existing telemetry pipeline.
- Decouple persistence from SignalR subscribers and frontend activity.
- Reuse existing authenticated connections.
- Avoid additional cloud requests.

### Option B — Background read-only refresh

If WebSocket messages only report changes or omit electrical values:

Investigate whether the existing supported provider API allows periodic read-only retrieval of current electrical parameters.

If supported:

- Add bounded, configurable background refresh.
- Reuse existing authentication and connection management.
- Avoid duplicate calls when several frontend clients are connected.
- Respect the provider's documented request limits.
- Respect existing throttling and retry behavior.
- Avoid polling unsupported devices.

### Option C — Provider limitations

If the provider does not expose reliable background electrical measurements:

- Document the restriction accurately.
- Implement the safest available partial solution.
- Expose the collection limitation through diagnostics.
- Do not fabricate periodic readings.
- Do not create a simulated heartbeat with repeated power values.
- Do not use undocumented control commands to force reports.

Do not assume that a WebSocket connection guarantees periodic power samples.

Choose the simplest reliable solution supported by the actual provider behavior.

## 4. Target devices and capabilities

Support electrical-monitoring devices recognized by the existing capability model.

Initial focus:

- Tongou PSF-X67 and compatible electrical relays.
- Sonoff devices exposing electrical power.
- Other eWeLink power-monitoring devices already supported by HomeAutomation.

Collect available reliable metrics:

- Active power (W).
- Voltage (V).
- Current (A).
- Energy counters (Wh/kWh), if actually exposed.
- Relay ON/OFF state, when reported.

Use canonical measurement units.

Do not hardcode the implementation to one Tongou model.

Do not invent energy counters that the provider does not expose.

Devices without electrical measurement capability must not generate power observations.

Do not change physical relay states.

## 5. Background collection lifecycle

Background telemetry must be independent from frontend usage.

Requirements:

- Start after the application's normal initialization.
- Respect migration readiness.
- Respect property and provider configuration.
- Maintain appropriate collection while no browser is connected.
- Recover after provider WebSocket disconnections.
- Recover after authentication refresh.
- Recover after backend restart.
- Support graceful shutdown.
- Respect cancellation tokens.
- Avoid overlapping collection requests.
- Avoid duplicate workers or duplicate polling for the same device.
- Bound concurrent cloud requests.
- Avoid blocking other providers.

If the current eWeLink worker already performs part of this work, extend it rather than creating a competing worker.

No new background service should be introduced without a clear architectural reason.

## 6. Collection interval and rate limiting

Collection frequency must be configurable.

Analyze the provider's limits before deciding the final schedule.

Suggested starting target:

- Approximately one electrical observation every 5 minutes, if supported reliably and within provider limits.

This is a target, not a requirement to make unsupported API calls.

A faster interval may be appropriate for genuinely pushed WebSocket readings that incur no additional cloud requests.

The implementation must:

- Reuse spontaneous WebSocket updates.
- Avoid unnecessary read-only requests.
- Rate-limit periodic refresh.
- Apply bounded exponential backoff with jitter where appropriate.
- Honor throttling responses.
- Avoid immediate request bursts after restart.
- Handle simultaneous devices efficiently.
- Avoid calling the same device repeatedly because multiple UI clients are connected.

Expose a configuration option for the collection interval.

Document the expected cloud request rate for 5, 10, 20 and 50 supported devices.

Do not make polling more aggressive simply to improve historical chart coverage.

## 7. Historical telemetry persistence

Reuse the existing telemetry infrastructure.

Relevant concepts:

- Physical devices.
- Device endpoints.
- Telemetry series.
- Raw samples.
- Latest readings.
- Hourly aggregates.
- Logical-device assignments.

Requirements:

- Persist through the existing TelemetryWriter path.
- Preserve physical-device identity.
- Preserve metric and canonical units.
- Preserve timestamp provenance.
- Use provider observation timestamps when reliable.
- Otherwise use the actual collection timestamp and mark its provenance.
- Handle duplicate observations idempotently.
- Reject NaN, infinity and invalid provider values.
- Never convert missing values to zero.
- Never persist repeated stale values as new observations merely because a timer fired.

Important distinction:

A successful read-only device refresh that retrieves a genuinely current measurement may be persisted as a new observation, even when its numeric value equals the previous value.

A cached or unchanged historical provider value must not be represented as a newly observed measurement without evidence that it is current.

Document how the implementation distinguishes these cases.

Do not rewrite historical raw samples.

## 8. Avoid duplicate provider streams

Review the existing eWeLink source-key and series selection conventions.

Ensure:

- WebSocket and background-refresh paths do not double-count the same observation.
- Multiple sources are selected consistently.
- The existing metric and device identity model is preserved.
- Repeated provider messages do not create artificial samples.
- Historical reads remain deterministic.
- Logical-device history does not accidentally merge unrelated electrical streams.

Prefer reusing the existing canonical eWeLink telemetry series when its semantics permit.

If an additional source is unavoidable, explicitly define source precedence and document the reason.

## 9. Energy estimation and 7A integration

Milestone 7A estimates energy by integrating power over supported observations.

The new collection mechanism should improve the availability of trustworthy power observations.

Do not modify the established energy-calculation rules merely to make the graphs look more complete.

Requirements:

- Preserve measured versus estimated energy separation.
- Preserve missing-data intervals.
- Preserve data-coverage calculations.
- Preserve the existing 80% completeness threshold.
- Never estimate energy through arbitrarily long gaps.
- Never turn an offline device into zero consumption.
- Never sum device counters across replacement boundaries.
- Preserve signed power semantics where applicable.

Review the existing eWeLink electrical freshness window.

The 7A report indicated an electrical freshness limit around 2 minutes.

If the new collection interval is 5 minutes, that may result in inherently low reported coverage.

Address this deliberately:

- Investigate whether the actual collection cadence supports a different freshness/maximum-hold policy.
- Distinguish current-value staleness from historical integration eligibility when their semantics differ.
- Do not increase coverage simply by extending the hold interval without evidence.
- Do not introduce artificial samples to bridge gaps.
- Prefer honest coverage over attractive but inaccurate energy totals.

If reliable integration cannot be achieved at the supported polling frequency, expose estimated energy as partial rather than complete.

## 10. WebSocket reliability

Inspect the existing eWeLink WebSocket worker.

Preserve its working authentication and session management.

Improve only what is necessary for reliable data collection.

Cover:

- Connection established.
- Authenticated.
- Disconnected.
- Reconnecting.
- Authentication expired.
- Rate limited.
- Device offline.
- Unsupported electrical capability.
- Missing electrical fields.
- Malformed messages.

Ensure that an individual device failure does not stop collection for unrelated devices.

Do not reconnect aggressively.

Do not expose authentication tokens or secrets in logs.

Do not alter OAuth grants or Data Protection key storage.

## 11. Operational diagnostics

Add useful structured diagnostic events for collection activity.

At minimum expose or log:

- Provider connection state.
- Number of eligible electrical devices.
- Background collection mode (WebSocket, refresh, or mixed).
- Last successful electrical observation per device.
- Last successful background refresh.
- Throttling and retry events.
- Unsupported electrical measurement capabilities.
- Skipped observations and their reason.

Use existing logging and health conventions.

Keep logs bounded and avoid recording every unchanged sensor value at Information level.

Never log provider credentials, secrets, tokens or full sensitive payloads.

Prefer structured counters and aggregate diagnostic summaries over verbose payload logging.

Make it possible to verify that electrical collection is working with zero connected frontend clients.

## 12. Configuration

Use existing provider configuration conventions.

If additional settings are necessary, support appropriate options such as:

- Enable continuous electrical monitoring.
- Background refresh interval.
- Maximum concurrent read-only requests.
- Retry/backoff settings.
- Device eligibility filtering.

Preserve existing defaults and configuration compatibility.

Do not introduce a new credential storage mechanism.

Do not change Configuration Vault encryption, sources or secrets.

Document any new operational settings and their activation requirements.

If database-configured operational settings already support the needed values, reuse them.

## 13. Frontend compatibility

Do not redesign the frontend.

Existing functionality must continue to work:

- eWeLink monitoring.
- Device status.
- Dashboard.
- Power measurements.
- Historical charts.
- Logical-device history.
- SignalR updates.

The frontend must not be responsible for activating historical data collection.

Opening or closing a page should not change whether the backend collects power readings.

If minor UI changes are needed to communicate unsupported data or low coverage, keep them small and consistent with existing design patterns.

Historical charts should automatically benefit from newly collected samples without introducing a separate history format.

## 14. Tests

Add focused automated tests.

### Collection behavior

- Backend collects electrical telemetry without frontend clients.
- No SignalR clients connected.
- Multiple frontend clients do not multiply provider requests.
- Power-monitoring device eligibility.
- Non-electrical devices ignored.
- Supported electrical values persisted.
- Unsupported metrics omitted.
- WebSocket-only collection.
- Refresh-based collection.
- Mixed WebSocket and refresh behavior.
- Repeated equal-valued but genuinely fresh readings.
- Repeated stale observations rejected.
- Duplicate provider messages.
- Provider and device timestamp handling.

### Reliability

- WebSocket disconnect and reconnect.
- Authentication expiration.
- Rate limiting.
- Backoff behavior.
- Temporary network failures.
- Individual device offline.
- Malformed provider response.
- Backend restart.
- Worker cancellation.
- Simultaneous device refreshes.
- No duplicate workers.

### Historical integration

- eWeLink power samples appear in existing raw history.
- Hourly rollups reflect new observations.
- Daily energy estimates use valid coverage.
- No invented zero readings.
- No artificial coverage improvement.
- Logical-device history remains correct.
- Physical device replacements remain correct.
- Data gaps remain visible in the 7C charts.

### Security and compatibility

- No device-control commands.
- No unexpected credential writes.
- No Configuration Vault changes.
- No cross-property telemetry leakage.
- Existing eWeLink monitoring preserved.
- Existing polling and provider integrations preserved.

Use synthetic messages, mocked eWeLink clients and controlled time.

Do not contact the real provider or physical devices during testing.

Use a disposable MySQL database for integration tests where needed.

## 15. Performance and scalability

The initial installation contains relatively few devices, but design for dozens of eWeLink devices.

Requirements:

- Bounded polling.
- Bounded concurrency.
- No unnecessary repeated requests.
- Efficient persistence.
- No unbounded in-memory queues.
- Predictable behavior under provider throttling.
- Minimal additional CPU and memory usage.
- Safe behavior when devices remain offline for long periods.

Do not introduce Kafka, Redis, a new message broker or an external telemetry database.

Stay within the existing HomeAutomation architecture.

## 16. Deployment compatibility and safety

Milestones 7A–7C are committed locally but may not yet be deployed.

Do not modify migrations 014–018.

Create a new migration only if absolutely necessary.

Do not apply migrations to the live database.

Do not run historical backfill on live data.

Do not initialize logical devices on live data.

Do not restart the running HomeAutomation backend.

Do not change provider credentials or Configuration Vault settings.

Do not make live cloud requests.

Do not control physical relays.

Do not push Git commits.

Any testing requiring provider interaction must use mocks, fixtures or locally simulated responses.

If the real provider's behavior cannot be verified without live calls, explicitly document the unresolved assumption and provide a safe manual verification procedure for later.

Do not claim continuous background collection is confirmed against real hardware based only on mocked tests.

## 17. Documentation

Update the relevant documentation.

Include:

- Root cause of the previous live-view dependency.
- Actual collection architecture.
- WebSocket versus refresh behavior.
- Supported electrical measurements.
- Device eligibility rules.
- Collection interval and cloud request budget.
- Timestamp and freshness semantics.
- Historical coverage implications.
- Retry and throttling behavior.
- New configuration options.
- Operational diagnostics.
- Safe deployment instructions.
- Manual verification procedure.

Describe how to verify 24-hour background collection later, with no browser connected.

Provide commands or diagnostic steps that do not expose credentials.

Document provider limitations honestly.

## 18. Completion

At completion:

1. Summarize the root cause found in the existing implementation.
2. Explain the chosen collection mechanism.
3. List modified backend components.
4. List configuration changes and defaults.
5. Report any database migration changes.
6. Document provider request volume and rate-limit behavior.
7. Explain timestamp and freshness handling.
8. Explain 7A, 7B and 7C integration.
9. Report builds and tests.
10. Identify unresolved live-provider assumptions.
11. Provide a safe deployment and manual verification checklist.
12. Update architecture documentation and ADRs as needed.
13. Update `docs/AI_HANDOFF.md`.
14. Archive this prompt under the next numbered file in `docs/prompts`.
15. Commit locally with a descriptive commit message.
16. Do not push.

Do not automatically proceed to Milestone 7E or unrelated tasks.

The result must provide reliable background electrical telemetry collection without browser dependencies, unnecessary cloud traffic, invented measurements or unsafe device operations.

## Execution summary

**Root cause (verified in the code and in CoolKit's UIID 5/32 protocol)**
- UIID 5/32 meters report power, voltage and current only inside a `uiActive` window, and only on changes larger than 2 W, 5 V or 0.03 A.
- The backend sent `uiActive` only while an operator's eWeLink page held a viewing lease.
- HTTP inventory values are cloud-held, carry no measurement time, and are correctly kept out of history.
- With no browser open, no electrical history was recorded.
- A per-field receipt-time de-duplication rule also dropped unchanged fields inside genuinely new reports.

**Solution: Option B over the existing socket**
- `Ewelink:ContinuousElectricalCollection` (default off) and `Ewelink:ElectricalCollectionSeconds` (default 900, range 300–3,600) let the existing socket worker's single activation loop (`EwelinkElectricalCollector`) open one 60-second window per eligible meter per interval. No browser is involved.
- **Restart:** a startup delay and a stable per-device stagger prevent bursts; the in-memory schedule simply restarts.
- **Failures:**
  - CoolKit 504 backs off only that meter, exponentially with jitter, up to 6 hours.
  - With collection on, a lost acknowledgement backs off that meter, and three in a row reconnect the socket.
  - Other rejections keep the existing process-wide stop.
- **Budget:** the interval stretches to fit the shared regional budget, and electrical activations pause each UTC day at 1,406 requests.
- **Eligibility:** owned, assigned, online, readable UIID 5 or 32 meters.
- **Samples:** they come only from device frames and use receipt-time provenance. A frame is a repeated delivery only when all its fields repeat within one activation window. The 2-minute history hold is unchanged, so coverage and estimated energy stay honestly partial.
- **Diagnostics:** a mode line at start and a 15-minute aggregate summary. A SignalR failure no longer ends the collection session.
- **Unchanged:** no migration, no new worker, no credential or vault-encryption change. Two optional vault operational fields keep older imported profiles loadable. The card shows two new status strings.

**Verification**
- **Backend:** Release build passed with zero warnings. Tests reported 287 passes and 11 opt-in skips with disposable MySQL 8.0.46 fixtures, twice in a row; without fixtures, 272 passes and 26 skips.
- **Frontend:** production build passed and Vitest reported 83 passes.
- **Not touched:** the live database, the running backend, the providers and the devices.
- **Unverified on real hardware:**
  - whether meters report at window start;
  - whether CoolKit counts WebSocket updates against its quota;
  - whether non-PSF-X67 UIID 5/32 models accept `uiActive`.

**References**
- [Design note](../architecture/13-ewelink-electrical-collection.md)
- [Integration guide: deployment and 24-hour verification](../integrations/ewelink-cloud.md#background-electrical-collection-milestone-7d)
- Code: `src/backend/HomeAutomation.Infrastructure/Ewelink/EwelinkElectricalReporting.cs`, `EwelinkSocketWorker.cs`, `EwelinkMapping.cs`, `EwelinkStore.cs`, `EwelinkClient.cs` and `Configuration/ProviderOptionMapping.cs`
- Tests: `tests/HomeAutomation.UnitTests/EwelinkCollectionTests.cs` and `EwelinkCollectionMySqlTests.cs`
