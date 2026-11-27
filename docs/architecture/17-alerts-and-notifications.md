# Alert rules, incidents and notifications

Alert rules watch telemetry and device events, open and escalate incidents, and notify property members in the app and in the browser. Migration 021 adds the tables. Alerts never send device commands: evaluation and equipment control are separate (see [future automation](#future-automation-integration)). The decision is recorded in [ADR 0008](adr/0008-alert-rules-and-incidents.md).

The prompt that introduced this ([0065](../prompts/0065-event-rules-incidents-notifications.md)) extends an earlier notification specification that was never archived. Its retained requirements were taken from that prompt's Phase 15 list: SignalR, the bell, Critical banners, browser notifications, background-tab awareness, preferences, history, a mobile push extension point, authorization, reliability and security.

## Four separate concepts

| Concept | Where | What it is |
| --- | --- | --- |
| Signal | `latest_readings`, `device_state`, `device_alerts` | An observed value with device or installation identity, property, type, unit, source and receipt time. Freshness and quality are derived at evaluation. Device events are offline transitions and fault codes. |
| Rule | `alert_rules`, `alert_rule_revisions`, `alert_rule_state` | A validated, structured definition plus severity, timing, correlation and notification policy. Revisioned; soft-deleted. |
| Incident | `alert_incidents`, `alert_incident_rules`, `alert_incident_events` | One tracked occurrence per property and correlation family, with contributing rules, severity history and an evidence timeline. |
| Notification | `notifications`, `notification_recipients`, `notification_deliveries`, `notification_preferences` | One message per notifiable incident event, a recipient row per member (read state, in-app visibility), and one delivery row per optional channel attempt. |

## Rule definition format

Definitions are JSON stored in `alert_rules.definition`, validated on save and never executed:

```json
{ "operator": "and", "children": [
  { "condition": { "id": "c1", "signals": [{ "kind": "measurement", "deviceId": 12, "endpointKey": "ac", "metric": "gridVoltage" },
                                          { "kind": "measurement", "deviceId": 13, "endpointKey": "ac", "metric": "gridVoltage" }],
                   "aggregation": "all", "operator": "lessThanOrEqual", "number": 10, "unit": "V", "recoveryNumber": 180,
                   "freshnessSeconds": 900 } },
  { "group": { "operator": "or", "children": [ { "condition": { "...": "..." } } ] } } ] }
```

- **Signals.** These four kinds are supported:
  - **Measurement on a device endpoint:** `deviceId`, `endpointKey` and `metric`.
  - **Measurement on an installation:** `installationId` and `metric`.
  - **Connectivity:** `deviceId`, with the values Online, Stale, Offline or Unknown. It is the application availability the live views show ([availability](18-availability-pv-installation-summary.md#device-availability)): a provider Offline flag that arrives with fresh readings reads Online, and Stale is never Offline. Rules stored with Online, Offline or Unknown keep working.
  - **Device event:** `deviceId` and `eventCode`, which is `DEVICE_OFFLINE`, `ANY_FAULT` or a reported fault code.

  Signals refer to device identity and capability, never to display names. Saving a rule rejects any device or installation that is not currently in the rule's property, and any measurement the device has not reported.
- **Signal identity and names.**
  - **Key:** a signal's stable identity is `AlertSignals.Key`, derived only from the `SignalRef` fields: `m:d{device}:{endpoint}:{metric}`, `m:i{installation}:{metric}`, `c:d{device}` or `e:d{device}:{eventCode}`.
  - **Spelling:** metrics use their JSON (camelCase) spelling, for example `m:d21:main:batterySoc`.
  - **Who uses it:** the catalog sends the key, and the editor, rule list, templates, grid outage setup and preview all resolve a stored `SignalRef` through one index built from each catalog entry's own `signal` (`signalIndex` in `shared/alerts.ts`).
  - **Persistence:** keys are not persisted, and labels are never identity. Entries with the same key are one signal.
  - **Equal device names:** labels use the custom name when set. When two current devices share a name, both keep it and gain the first attribute that tells them apart: model, then manufacturer, then room, then the device number, for example *Heatpump (Altherma)* and *Heatpump (PSF-X67)*. Measurement labels that still clash gain their channel.
  - **Agreement:** evaluation evidence, incident timelines and the preview use the same names.
- **Operators by value type.**

  | Value type | Operators | Examples |
  | --- | --- | --- |
  | Numbers | below, at or below, above, at or above, equals, does not equal | — |
  | On/off values | is on, is off, changed | `GridAvailable`, `SwitchOn`, `HeatingWarning`, `HeatingEmergency` |
  | States | equals, does not equal, changed | `OperatingMode`, `WarningStatus`, `HeatingControlMode`, `WaterSetpointMode`; connectivity offers only equals and does not equal |
  | Events | occurred | — |

  Numeric comparisons are never offered for other types. *Changed* and *occurred* need a look-back window of 1 minute to 24 hours.
- **Units.** A numeric condition's unit must be the metric's unit (for example V, %, °C). Percentages must lie between 0 and 100.
- **Aggregation.** A condition with several signals of one metric uses `single` (exactly one signal, for example a designated authoritative inverter), `any` or `all`. There is no free-form aggregation language.
- **Limits.** Groups nest at most three levels deep, with at most 8 items per group, 12 conditions per rule and 8 signals per condition.
- **Hysteresis.** `recoveryNumber` must lie on the recovered side of `number`. While the rule is active, a below-threshold condition stays true until the value rises above the recovery value; an above-threshold condition, until it falls below.
- **Rule-level settings:**
  - `activationSeconds` and `recoverySeconds`: 0 to 24 hours.
  - Severity: `info`, `warning` or `critical`.
  - `correlationKey`, for example `grid.outage` or `grid.outage:north`: optional, lower-case.
  - `incidentTitle`.
  - Notification policy: `onActivation`, `onEscalation`, `onDeescalation`, `onResolution`, `reminderMinutes` (0, or 5 to 1440) and an optional `suppressWhileActive` family.

## Condition evaluation, unknown and stale data

Each signal evaluates to True, False or Unknown. Unknown is never treated as zero or false. A signal is Unknown when any of these holds:
- its device moved to another property, was removed or is simulated;
- no reading exists;
- the reading is more than 2 minutes in the future;
- the value has the wrong type;
- the latest observation is older than the condition's maximum age (`freshnessSeconds`, 30 seconds to 7 days).

The editor suggests three provider cadences as the maximum age, and at least 10 minutes:

| Provider | Report cadence | Suggested maximum age |
| --- | --- | --- |
| Growatt | 5 min | 15 min |
| Daikin | 15 min | 45 min |
| eWeLink | 5 min | 15 min |
| Tuya | 30 min | 90 min |

A device-event condition is True when at least one matching event was recorded in its window, and False otherwise.

Groups use Kleene logic:
- **AND** is False if any child is False, otherwise Unknown if any child is Unknown, otherwise True.
- **OR** is True if any child is True, otherwise Unknown if any child is Unknown, otherwise False.

`all` and `any` aggregation follow the same rules. So 0 V now together with a battery SOC from yesterday is Unknown, and never opens the Critical composite.

Ordering comes from storage: `latest_readings` only moves forward in observation time. A late or replayed older sample is stored in history but never becomes the value a rule sees. Duplicate samples are ignored by the sample primary key.

## Detection state, durations and recovery

`alert_rule_state` persists each rule's phase: inactive, pending, active or recovering.

- **Activation:** the result must stay True for `activationSeconds`, measured on the server clock across evaluations. False or Unknown cancels a pending activation.
- **Recovery:** the result must stay False for `recoverySeconds`. True cancels a pending recovery. Unknown neither confirms nor cancels it, so missing data never resolves an incident, and an active rule stays active while its inputs are Unknown.
- **Provider cadence:** a confirmation shorter than the provider cadence completes at the next evaluation while the same sample is still fresh.
- **Restarts:** the state survives restarts, so an ongoing outage keeps its incident.

## Correlation and severity

- **Incident identity.** The identity is the property plus the correlation key, which defaults to `rule:{id}` for an independent incident, plus the current active lifecycle. A generated `active_key` column with a unique index allows at most one active incident per property and family. Unrelated faults are never merged: only rules naming the same family share an incident.
- **Severity selection.**
  - The incident's severity is the highest severity among its currently active contributing rules.
  - Opening keeps the earliest activation time. Every activation and clearing of a contributing rule is an event, with the evidence: each signal's value, observation time, freshness and result.
  - An escalation records the rule that caused it and clears any acknowledgment, because a new Critical condition needs attention again.
  - When the higher rule clears (after its recovery confirmation and hysteresis), the remaining rules are reevaluated and the incident de-escalates.
  - It resolves only when no contributing rule is active, and the resolution records the duration.
- **Independent incidents and suppression (the default policy).**
  - A battery rule without a correlation group is its own incident, even during a grid outage.
  - Nothing is suppressed by default. A rule can explicitly set `suppressWhileActive` to another family. Its notifications are then recorded as suppressed while that family has an active incident, and its incident and timeline are still kept.
  - The templates give the grid rules the family `grid.outage` and the battery rules `battery.low`. That family escalates from *Battery low* to *Battery critically low*.

Worked example (Countryside House, templates):

| Time | Readings | Result |
| --- | --- | --- |
| 13:20 | 0 V on both inverters, 78% | *Grid power lost* is pending (15 s). |
| 13:20:20 | (no change) | One Warning incident, *Grid power unavailable*. The Warning notification is sent. |
| 13:25 | 49% | *Grid outage with low battery* is pending (60 s). |
| 13:26 | (no change) | The same incident escalates to Critical. The escalation notification is sent and the acknowledgment cleared. |
| 13:30 | 52% | Still Critical (recovery is above 55%). |
| 13:35 | 58% | After 2 minutes, de-escalates to Warning. This is recorded, and the template does not notify on de-escalation. |
| 13:40 | 228 V | After 1 minute, resolved with the duration. An Info recovery notification is sent. |

The MySQL test `AGridOutageOpensAsWarningEscalatesDeescalatesAndResolvesAsOneIncident` replays exactly this.

## Rule changes

Every create, update, enable, disable and delete writes a full revision snapshot. Updates and deletions require the revision the user reviewed, and a stale one returns 409.

| Change | Effect |
| --- | --- |
| Edit | Detection continues. The next evaluation applies the new revision to the current state, so a severity change escalates or de-escalates the open incident, recording the revision. A changed correlation group moves the contribution and records `ruleChanged`. |
| Disable or delete | The rule's state is reset and its contribution clears with `ruleDisabled` or `ruleDeleted`. An incident left without active rules closes with that resolution, not as a recovery, and its notification says so. |
| Delete | Soft: incidents, timelines and notifications keep the rule's name and revision. |
| Re-enable | Checks the signal references again, so a rule whose device moved must be edited first. |
| Duplicate | Creates a disabled copy. |

A referenced device that moves to another property or is removed makes its conditions Unknown. The rule list warns about it, and history is unchanged.

## Evaluation, workers and performance

- **Triggering.**
  - `TelemetryWriter` wakes the evaluator for a property after each committed write (Growatt, Daikin, eWeLink, Tuya). Writes are coalesced for 1 second.
  - A sweep every `Alerts:SweepSeconds` (default 30) evaluates properties with enabled rules or active incidents. It handles durations, freshness expiry, reminders, delivery retries and delivery expiry.
  - A property without rules or active incidents writes nothing.
- **What one evaluation reads and does.**
  - It reads only that property's enabled rules and only the referenced signals' latest values: one indexed query per signal kind, plus bounded windows for events (at most 2,000 rows) and state changes (at most 501 samples per series and window). It never scans history.
  - It parses rule definitions once per rule and revision; that cache is bounded and invalidated by the revision.
  - It runs in one transaction. A row in `alert_evaluation_locks` is locked first, so concurrent workers, telemetry triggers and the sweep serialize per property. The unique active-incident key is the database-level backstop. Deadlocks are retried.
- **Independence.** Processing runs in the background worker whether or not a browser is open. No message broker is involved; real-time refresh uses the existing SignalR hub.

## Notification delivery

- **Notifiable events.** Opening, escalation, de-escalation, recovery and reminders notify according to the rule policy. Repeated observations create no new events and no notifications. Each notification is unique per incident event.
- **Rate limits.** Non-Critical escalations and de-escalations of one incident are rate-limited to one per `Alerts:MinimumRenotifySeconds` (default 300); the rest are recorded as `rateLimited`.
- **Reminders.** They repeat at the shortest active contributor interval and are deduplicated by sequence.
- **Recipients.** Every member of the property receives a recipient row. Read state belongs to the recipient, and acknowledgment belongs to the incident. Acknowledging never stops detection.
- **Preferences.** Each user has preferences per severity (in-app, browser, sound for Critical) and per event type. Critical in-app delivery cannot be turned off. Defaults:
  - Info: in-app only.
  - Warning and Critical: in-app and browser.
  - Sound: off.
- **Channels.** In-app visibility is the recipient row. Optional channels implement `INotificationChannel` and get a `notification_deliveries` row: pending, then sent, failed, unsupported or expired, with up to 3 attempts.
  - A conditional claim ensures a delivery is attempted once even with several dispatchers.
  - A row for a channel that is not installed becomes `unsupported`.
  - Deliveries not handed over within `Alerts:DeliveryExpirySeconds` (default 900) expire, rather than appear late.
- **Browser channel.** SignalR sends `browserNotification` to the recipient's open sessions, and the delivery becomes `sent`.
  - **Background tab with permission:** the page shows a system notification. Critical notifications require interaction.
  - **Visible tab:** no system notification is shown; the bell and banner already show it.
  - **Reporting:** the page reports `shown`, or `notShown` with `visible`, `permission`, `unsupported` or `error`. That becomes `delivered` or `notShown`.
  - **Sound:** when enabled, Critical notifications play a short chime.
- **Live refresh.** `alertsChanged` (property group) and `notificationsChanged` (the affected users) refresh open views.
- **Critical visibility.** The header banner lists active Critical incidents in all of the user's properties until they resolve, whether or not they were read.
- **Read state.** It is stored per user in `notification_recipients.read_at_utc` and is separate from acknowledgment and resolution: reading never acknowledges or resolves, and acknowledging never reads.
  - **Bell:** opening it reads nothing. Selecting a notification opens its incident and marks that one notification read.
  - **Notification history:** a title link and **Mark read** read one notification, and **Mark all read** (bell and history) reads them all.
  - **Direct links:** a link that carries `notification=<id>` (a system notification click) marks exactly that notification read once. A plain incident link reads nothing, because an incident may have several notifications.
  - **Display:** counters and styles change only after the server confirms. A failure is shown and the notification stays unread. Repeated clicks send one request.
  - **Other tabs:** they follow through `notificationsChanged`. Every refresh first cancels in-flight notification reads so a stale count cannot survive (`refreshNotifications`).

### Mobile push extension point

A push channel is a new `INotificationChannel` named for example `push`, plus a device-registration store and a preference flag. The dispatcher, delivery ledger, claim, retries, expiry and outcome reporting are channel-neutral. The test `DeliveriesReachEachRecipientsChannelsOnceAndUnknownChannelsAreRecordedAsUnsupported` exercises a registered fake `push` channel and an uninstalled one. No push provider is chosen yet.

## API

**Property routes** are under `/api/v1/properties/{id}`, behind the membership filter. Non-members get 404. Any member can read; Owner, Admin and Operator can change rules and acknowledge, as for the shared dashboard.

| Route | Purpose |
| --- | --- |
| `GET/POST alert-rules` | List or create rules. |
| `POST alert-rules/batch` | The explicit grid outage setup. |
| `GET/PUT alert-rules/{rule}` | Read or update a rule (`expectedRevision`). |
| `POST alert-rules/{rule}/enabled` | Enable or disable. |
| `POST alert-rules/{rule}/duplicate` | Disabled copy. |
| `DELETE alert-rules/{rule}?revision=` | Soft delete. |
| `GET alert-rules/{rule}/revisions` | Revision history. |
| `GET alert-rules/catalog` | Signals the editor can offer. |
| `GET alert-rules/templates` | Templates and the grid outage setup. |
| `POST alert-rules/preview` | Read-only evaluation. |
| `GET incidents` | Filters: `status`, `severity` (highest reached), `ruleId`, `deviceId`, `from`, `to`. |
| `GET incidents/{id}` | Incident with its timeline. |
| `POST incidents/{id}/acknowledge` | Acknowledge. |

**User routes** are under `/api/v1/notifications`.

| Route | Purpose |
| --- | --- |
| `GET` | Filters: property, device, kind, severity, rule, dates, incident status, unread, in-app; keyset pages with `before`. |
| `GET summary` | Unread counts and active Critical incidents. |
| `POST read` | Mark notifications read. |
| `POST {id}/delivery` | Browser outcome. |
| `GET/PUT preferences` | Notification preferences. |

Every mutation needs the antiforgery token. Until migration 021 is recorded, these routes answer 503 `schemaPending` and the worker waits. Base readiness does not depend on 021.

## Rule editor and screens

- **Automations: Alert rules.**
  - **Rule list:** name, property, enabled switch, severity (icon and text), condition summary, last triggered and incident state. It filters by property, severity and status, and shows warnings for moved devices.
  - **Templates:** each is offered only when its signals exist. A template opens the editor disabled, with its devices and thresholds proposed for review.
  - **Grid outage setup:** *Set up grid outage alerts* creates the two grid rules after a confirmation that lists them with their device names, comparisons and thresholds. Confirmation stays disabled until signal names have loaded.
  - **Device actions** remain a placeholder there.
- **Editor.**
  - **Conditions:** nested AND/OR groups. Each condition selects a signal from the property catalog, can add more signals of the same metric with every/any, and offers only valid comparisons. It shows units, recovery thresholds, the look-back window, the maximum age and the signal's latest reading (or *No reading yet*).
  - **Unavailable signals:** a stored signal the property no longer offers is shown as *Unavailable signal* with an explanation. It is kept unchanged on save and never replaced by another signal; its threshold stays editable. Missing or stale readings of an offered signal are a different state: they make the condition Unknown.
  - **Other settings:** severity, correlation group and incident title, timing, and the notification policy.
  - **Summary:** a sentence such as *When Inverter A · AC input voltage is at or below 10 V AND Battery · Battery SOC is below 50% for 1 min, create or escalate a Critical grid power unavailable incident.*
  - **Validation:** problems are explained before saving and checked again by the server.
  - **Preview with latest readings:** for each signal, the value, observation time, freshness and True/False/Unknown, plus the overall result. Signals carry the same names as in the editor. It never writes.
- **Alerts page.**
  - **Incidents:** active or resolved, with highest severity and date filters. The detail shows contributing rules, the timeline with evidence and observation times, and acknowledgment.
  - **Notification history:** filters by property, severity, event, incident status, read state and dates; shows delivery outcomes; can mark notifications read; loads more pages.
  - **Notification settings.**
- **Header.** The bell shows unread, Warning and Critical counts, recent notifications and *Mark all read*. The Critical banner sits above every page.

## Templates

| Template | Condition | Severity | Family | Confirm and recover |
| --- | --- | --- | --- | --- |
| Grid power lost | AC input voltage at or below 10 V (all inverters), recovers above 180 V | Warning | `grid.outage` | 15 s, 60 s |
| Grid outage with low battery | The above and battery SOC below 50%, recovers above 55% | Critical | `grid.outage` | 60 s, 120 s |
| Battery low | SOC below 35%, recovers above 40% | Warning | `battery.low` | 120 s, 300 s |
| Battery critically low | SOC below 20%, recovers above 25% | Critical | `battery.low` | 60 s, 300 s |
| High room temperature | Above 30 °C, recovers below 28 °C (editable) | Warning | none | 600 s, 300 s |
| Low room temperature | Below 10 °C, recovers above 12 °C (editable) | Warning | none | 600 s, 300 s |
| Device offline | Connectivity equals Offline | Warning | none | 300 s, 60 s |

Templates appear only when the property reports the needed signals. Nothing is created or enabled automatically.

## Future automation integration

- **Integration points.** Future automations can subscribe to incident events, for example *grid.outage escalated to Critical*, through `alert_incident_events`. That table is ordered, durable and carries evidence. Automations can also evaluate the same condition model.
- **Commands.** Any device command must go through the separate automation engine and the existing command ledger, with its own enablement, dry run, confirmation and readback ([automation engine](06-automation-engine.md), [ADR 0005](adr/0005-automation-execution.md)).
- **Separation.** An alert rule can never issue a command, and no code path from evaluation reaches a provider client.

## Deployment and operations

- **Deployment.**
  1. Back up the database.
  2. Apply 021 with the migration account (`--migrate`). It needs no new grant: rules are soft-deleted and nothing deletes rows.
  3. Restart the API. The worker starts evaluating when 021 is recorded.
- **Settings.** `Alerts:Enabled` (default true), `Alerts:SweepSeconds` (10–600), `Alerts:MinimumRenotifySeconds` (0–86400) and `Alerts:DeliveryExpirySeconds` (60–86400).
- **First use.** No rules exist until someone creates them or confirms the grid outage setup.

### Troubleshooting

| Symptom | Check |
| --- | --- |
| Alerts pages say the database must be upgraded | Migration 021 is not in `schema_migrations`. The log has "Alert evaluation waits for database migration 021". |
| A rule never triggers | In the editor preview, an Unknown with *Too old* means the maximum age is shorter than the provider's real cadence or the feed stopped. *Unavailable* means the device moved. The rule list shows *Inputs unknown*. |
| An incident does not resolve | Unknown inputs hold an active rule on purpose. Check feed freshness. Recovery also needs the recovery threshold and period. |
| No browser notification | Check *Notification settings*: browser permission, and the severity and event toggles. System notifications appear only while the tab is in the background. The history shows each delivery's status and reason. |
| Evaluation warnings in the log | `Alert evaluation for property {PropertyId} failed ({ErrorType})`: the next sweep retries. Logs never contain values, titles or user identifiers. |
