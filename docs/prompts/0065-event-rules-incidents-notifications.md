# HomeAutomation — Advanced Event Rules, Composite Conditions, Severity and Notification Architecture

Date: 2026-11-27

The prompt refers to a "previous centralized notifications specification". That text was never archived and is not in the repository, so it could not be imported from its original. The operator chose to treat the Phase 15 list below as the inherited requirements.

## Original prompt

# HomeAutomation — Advanced Event Rules, Composite Conditions, Severity and Notification Architecture

## Objective

Extend the planned HomeAutomation notification subsystem into a configurable, event-driven alerting and notification platform.

This requirement expands the previous centralized notifications specification. Implement both sets of requirements as one coherent subsystem, not two separate implementations.

The system must support:

- Multiple event sources and providers.
- Info, Warning and Critical severity levels.
- Single-condition rules.
- Composite rules using AND and OR.
- Multiple devices and telemetry signals.
- Duration-based conditions.
- Hysteresis and recovery logic.
- Event correlation.
- Incident escalation and de-escalation.
- Deduplication.
- Persistent notification history.
- Configurable notification delivery.
- Real-time browser notifications.
- Future mobile push support.
- User-configurable rules through an accessible web interface.

The architecture must allow new rules without requiring C# modifications for every simple threshold or combination of supported telemetry conditions.

Avoid implementing an excessively complex general-purpose scripting engine.

Use structured, validated rule definitions rather than arbitrary executable expressions.

---

# PHASE 1 — Separate Signals, Rules, Incidents and Notifications

Implement four clearly separated concepts.

## 1. Signals and domain events

A signal represents an observed value or state.

Examples:

- Growatt AC input voltage.
- Growatt battery SOC.
- Growatt inverter state.
- Daikin indoor temperature.
- Daikin operating mode.
- Tuya temperature.
- eWeLink relay state.
- Device connectivity.
- Provider synchronization status.

Signals must have:

- Stable identity.
- Device or installation identity.
- Property ownership.
- Value and data type.
- Unit where appropriate.
- Source timestamp.
- Observation/ingestion timestamp.
- Freshness information.
- Quality or validity information.

Domain events represent meaningful transitions or occurrences.

Examples:

- Grid power lost.
- Grid power restored.
- Device became offline.
- Device came online.
- Provider authentication failed.
- Automation execution failed.

Support both telemetry conditions and domain events as rule inputs.

## 2. Rules

Rules evaluate signals and events.

A rule may combine multiple conditions.

A rule defines:

- Identity and name.
- Description.
- Scope.
- Enabled/disabled state.
- Conditions.
- Severity.
- Detection timing.
- Recovery behavior.
- Correlation policy.
- Notification policy.
- Version.
- Creation and modification audit information.

## 3. Incidents

An incident represents an actual observed condition requiring tracking.

A single incident can:

- Start as Warning.
- Escalate to Critical.
- De-escalate when appropriate.
- Be acknowledged.
- Remain active.
- Be resolved.
- Retain its complete lifecycle history.

An incident must not be recreated whenever a new telemetry sample arrives.

## 4. Notification deliveries

Notification delivery is separate from incident state.

One incident may generate:

- Initial notification.
- Severity escalation notification.
- Optional reminder.
- Recovery notification.

The same incident may be delivered to multiple users through different channels.

---

# PHASE 2 — Generic Condition Model

Implement a structured, strongly validated condition model.

## 2.1 Supported comparison operators

Initial operators:

- Equals.
- Not equals.
- Less than.
- Less than or equal.
- Greater than.
- Greater than or equal.
- Boolean true/false.
- Event occurred.
- State changed.

Design for future operators, but implement only those needed.

## 2.2 Logical operators

Support:

- AND
- OR

Allow condition groups.

Example:

(A AND B) OR C

Support limited nested groups with explicit validation and a reasonable maximum complexity.

Do not evaluate user-provided C#, JavaScript, SQL or other executable code.

## 2.3 Example rule

Rule:

"Grid outage with low battery"

Conditions:

- AC input voltage <= 10 V.
- Battery SOC < 50%.

Operator:

AND

Severity:

Critical

Both conditions must be satisfied using valid, sufficiently fresh observations.

## 2.4 Cross-device conditions

Support conditions originating from different devices within the same property.

Example:

- Growatt AC input voltage <= 10 V.
- Tuya indoor temperature < 10°C.

Another example:

- Growatt battery SOC < 30%.
- eWeLink main heater relay = ON.

The system must resolve signals by stable device identity and capability, not fragile display names.

Reject unauthorized cross-property signal references.

Define how a rule behaves when a referenced device is moved, removed or becomes unavailable.

## 2.5 Scope and aggregation

Support property-scoped rules.

For an installation with multiple inverters, do not assume one arbitrary inverter's voltage represents the entire installation.

Support explicit signal selection and well-defined aggregation where required.

Examples:

- One selected inverter.
- Any selected inverter.
- All selected inverters.
- A designated authoritative source.

Use typed aggregation semantics and clearly document their behavior.

Do not introduce a generic aggregation language unnecessarily.

---

# PHASE 3 — Severity Model

Implement three notification severity levels:

### Info

Informational events.

Examples:

- Grid power restored.
- Device reconnected.
- Scheduled synchronization completed.

Default behavior:

- In-app notification.
- History entry.
- No intrusive warning banner.
- No sound by default.

### Warning

Conditions requiring attention.

Examples:

- Grid power lost while battery SOC remains healthy.
- Battery SOC below 35%.
- Temperature outside configured range.
- Device telemetry stale.

Default behavior:

- In-app notification.
- Visible warning indicator.
- Browser notification if enabled.

### Critical

High-impact situations requiring prompt attention.

Examples:

- Grid power lost AND battery SOC below 50%.
- Battery SOC below 20% during an outage.
- Inverter shutdown or critical fault.
- Dangerous temperature threshold reached.

Default behavior:

- Persisted active incident.
- Prominent in-app warning.
- Browser notification if enabled and permitted.
- Unread critical count.
- Optional alert sound.

Critical alerts must remain visible while active, regardless of whether the user marks their notifications as read.

Severity must be configurable per rule.

Use visual design that distinguishes severity without depending only on color.

---

# PHASE 4 — Composite Rules and Priority Escalation

This is a central requirement.

Multiple rules may describe different severity levels of the same underlying physical incident.

The system must support grouping these rules into one correlated incident.

## Example scenario

Property: Countryside House / Стъргел

Initial telemetry:

AC input voltage = 0 V.
Battery SOC = 78%.

Result:

One active incident:

"Grid power unavailable"

Severity: Warning.

Later:

AC input voltage = 0 V.
Battery SOC = 49%.

Result:

The existing incident escalates to Critical.

Do not create a second duplicate grid outage incident.

Record the escalation in the incident history.

Send an escalation notification according to the user's delivery preferences.

Later:

AC input voltage = 0 V.
Battery SOC = 58%.

Result:

The system may de-escalate to Warning after the configured confirmation period.

Do not repeatedly oscillate severity around threshold boundaries.

Finally:

AC input voltage = 228 V.

After confirmed recovery:

Resolve the incident.

Generate an informational recovery notification.

Record the outage duration and severity history.

## 4.1 Incident correlation

Introduce explicit correlation semantics.

A rule can reference an incident family or correlation group.

Example:

`grid.outage`

Rules in this group may contribute to the same incident.

Suggested identity dimensions:

- Property.
- Installation or grid connection.
- Incident family.
- Current active lifecycle.

Do not correlate unrelated faults simply because they occur at the same time.

## 4.2 Severity selection

When multiple correlated rules are active:

- Highest active severity determines incident severity.
- Keep track of all contributing rules.
- Preserve the original activation time.
- Record severity transitions.
- Record which conditions caused escalation.
- Avoid duplicate deliveries for repeated observations.

When a higher-severity rule stops matching:

- Reevaluate remaining rules.
- De-escalate if appropriate.
- Apply recovery hysteresis or confirmation.
- Do not resolve the entire incident while its base condition remains active.

## 4.3 Independent incidents

A battery warning may also be meaningful independently from a grid outage.

The system must distinguish between:

- Battery warning as an independent condition.
- Battery SOC contributing to a correlated grid outage incident.

Design an explicit correlation/suppression policy.

Do not automatically suppress every battery alert merely because a grid outage exists.

Prevent redundant notifications while retaining meaningful incident history.

Document the exact policy and use consistent, configurable defaults.

---

# PHASE 5 — Condition Duration, Freshness and Hysteresis

Rules must support timing parameters.

## 5.1 Duration conditions

Examples:

- AC input voltage below 10 V for 15 seconds.
- Battery SOC below 50% for 2 minutes.
- Temperature above 30°C for 10 minutes.
- Device offline for 5 minutes.

Allow configurable confirmation duration.

Respect actual provider sampling intervals.

## 5.2 Hysteresis

Allow separate activation and recovery thresholds.

Example:

Battery low:

- Activate below 35%.
- Recover above 40%.

Grid outage:

- Activate below 10 V.
- Recover above 180 V.

Prevent notification flapping.

## 5.3 Freshness

A composite rule must not combine fresh telemetry with arbitrarily old values.

Example:

- Grid voltage = 0 V from 13:30.
- Battery SOC = 45% from yesterday.

This must not trigger a new Critical composite incident as if both values were current.

Support per-condition freshness requirements.

Handle missing, invalid and stale data explicitly.

Use three-valued evaluation where appropriate:

- True
- False
- Unknown

Define logical AND/OR behavior for Unknown consistently.

Unknown must not be silently treated as zero or false.

## 5.4 Out-of-order telemetry

Older telemetry must not incorrectly reverse newer incident state.

Use source timestamps and suitable ordering rules.

Support provider delays and replay without duplicating notifications.

## 5.5 Backend restart

Persist sufficient incident and evaluation state to reconstruct active conditions safely after a restart.

Do not create a new incident for the same ongoing outage after restart.

---

# PHASE 6 — Rule Configuration and Persistence

Rules must be persisted in MySQL.

Implement an appropriate normalized or structured JSON representation consistent with existing conventions.

Recommended properties:

- Rule ID.
- Property ID.
- Rule name.
- Description.
- Enabled flag.
- Severity.
- Rule definition.
- Activation settings.
- Recovery settings.
- Correlation group.
- Notification policy.
- Revision/version.
- Created by.
- Created at.
- Updated by.
- Updated at.

Ensure:

- Strong validation.
- Concurrency-safe updates.
- Stable signal references.
- Database indexes for rule lookup.
- Rule versioning or audit trail.
- Backward-compatible schema evolution.

Do not store executable source code in the rule definition.

Support safe enable/disable operations.

Define whether changing an active rule reevaluates or closes related incidents; do not silently lose incident history.

---

# PHASE 7 — Rule Configuration UI

Implement a Rules section within the appropriate existing HomeAutomation navigation area.

Reuse the existing Automations UI where appropriate, but keep notification rules conceptually distinct from actions that control hardware.

## 7.1 Rules list

Display:

- Name.
- Property.
- Enabled/disabled.
- Severity.
- Short condition summary.
- Last triggered.
- Current incident state.

Provide filtering by property, severity and status.

## 7.2 Create/edit rule

Provide a visual rule editor.

Support:

- Rule name.
- Property selection.
- Device selection.
- Capability/signal selection.
- Comparison operator.
- Threshold.
- AND/OR condition grouping.
- Severity.
- Confirmation duration.
- Recovery threshold.
- Correlation group.
- Notification preferences.

The user must not have to enter raw JSON for ordinary rules.

Show human-readable summaries.

Example:

"When AC input voltage is below 10 V AND battery SOC is below 50%, create or escalate a Critical grid outage incident."

Validate units and signal types.

Do not offer numeric comparisons for unsupported data types.

Provide understandable validation messages.

## 7.3 Rule preview

Provide a read-only rule evaluation preview against the latest known telemetry.

Display:

- Each condition.
- Current observed value.
- Observation timestamp.
- Freshness.
- True/False/Unknown result.
- Overall rule result.

Preview must not generate real incidents or notifications.

## 7.4 Rule management

Support:

- Create.
- Edit.
- Enable/disable.
- Duplicate.
- Delete with confirmation.

Deleting a rule must not erase historical incidents or notifications.

Maintain authorization consistent with the property management model.

---

# PHASE 8 — Default Rule Templates

Provide predefined rule templates.

Templates are not automatically enabled for every property.

Initial templates:

## Grid power lost

Condition:

AC input voltage <= 10 V.

Severity:

Warning by default.

## Grid outage with low battery

Conditions:

AC input voltage <= 10 V
AND
Battery SOC < 50%.

Severity:

Critical.

Correlation:

Same grid outage incident family.

## Battery low

Condition:

Battery SOC < 35%.

Severity:

Warning.

## Battery critically low

Condition:

Battery SOC < 20%.

Severity:

Critical.

## High room temperature

Condition:

Temperature > configurable threshold.

Severity:

Warning.

## Low room temperature

Condition:

Temperature < configurable threshold.

Severity:

Warning.

## Device offline

Condition:

Connectivity lost for configured duration.

Severity:

Warning.

These templates must adapt to actual available capabilities.

Do not create rules referencing nonexistent devices or telemetry.

Allow the user to select devices and confirm thresholds before enabling templates.

Provide a minimal set of preconfigured rules only when necessary for the initial grid outage use case, through an explicit setup workflow.

Do not silently enable large numbers of alerts.

---

# PHASE 9 — Incident Lifecycle and User Interaction

An incident can be:

- Active.
- Resolved.

Track acknowledgment independently.

Store:

- Active severity.
- Original severity.
- Maximum severity reached.
- Start time.
- Last change time.
- Resolution time.
- Contributing rule IDs.
- Relevant measurements.
- Severity transition history.

Support:

- Mark as read.
- Acknowledge.
- View details.
- Inspect contributing conditions.
- Review historical severity changes.

Acknowledgment must not stop detection or automatically resolve the incident.

An escalated Critical condition may require a new notification even if the earlier Warning was already acknowledged.

---

# PHASE 10 — Notification Delivery Policies

A rule or incident type can define which transitions generate notifications.

Support:

- Initial activation.
- Severity escalation.
- Optional severity de-escalation.
- Resolution/recovery.
- Optional reminders for ongoing incidents.

Avoid a notification for every telemetry update.

Implement sensible deduplication and rate limiting.

Allow user-specific delivery preferences by severity and event type.

Critical incidents must remain visible in-app even if optional browser delivery is disabled.

Preserve the distinction between:

- Incident occurrence.
- Notification recipient.
- Read status.
- Acknowledgment.
- Channel delivery attempt.
- Actual delivery result where observable.

Keep the design compatible with future mobile push delivery.

---

# PHASE 11 — Notification Registry and History

Extend the previous notification persistence requirements.

The database must retain:

- Incident lifecycle.
- Rule evaluation transitions relevant to incidents.
- Severity changes.
- Contributing conditions.
- Observed values.
- Notification recipients.
- User acknowledgments.
- Delivery attempts and outcomes.

Provide a searchable history UI.

Support filtering by:

- Property.
- Device.
- Event type.
- Severity.
- Rule.
- Date range.
- Active/resolved.
- Read/unread.

Include a detailed timeline.

Example:

13:20 — Grid voltage dropped to 0 V.
13:21 — Outage confirmed; Warning activated.
13:46 — Battery SOC fell below 50%.
13:46 — Incident escalated to Critical.
14:05 — Grid power restored.
14:06 — Incident resolved.

These times are illustrative; real records must use actual observation and processing timestamps.

---

# PHASE 12 — Testing Requirements

Add tests for the generic rule engine and composite incident behavior.

Required scenarios:

1. One simple condition matches.
2. One simple condition does not match.
3. AND with both true.
4. AND with one false.
5. OR with one true.
6. Nested condition groups.
7. Unknown telemetry.
8. Stale telemetry.
9. Cross-device composite rule.
10. Unauthorized cross-property rule.
11. Numeric threshold and units.
12. Duration confirmation.
13. Hysteresis.
14. Out-of-order samples.
15. Duplicate telemetry ingestion.
16. Warning incident creation.
17. Critical incident creation.
18. Warning-to-Critical escalation.
19. Critical-to-Warning de-escalation.
20. Incident recovery.
21. Multiple correlated rules.
22. Independent incidents.
23. Notification deduplication.
24. Multiple recipients.
25. Backend restart during active incident.
26. Concurrent evaluation.
27. Rule version update.
28. Rule disabled during active incident.
29. Rule deletion with preserved history.
30. Rule preview without side effects.
31. Browser and SignalR delivery.
32. Persistence and reload of incidents.
33. Future-channel extensibility boundaries.

Use isolated disposable test databases.

Include frontend tests for the visual rule editor, validation, previews, incident timeline and notification settings.

Run existing regression suites.

---

# PHASE 13 — Performance and Reliability

The rule engine must support a growing number of properties, devices and rules.

Evaluate only relevant rules when new telemetry arrives where practical.

Avoid scanning all historical telemetry or every rule after each sample.

Cache validated rule definitions if appropriate, with reliable invalidation.

Avoid unbounded memory usage.

Ensure database-backed incident deduplication.

Support multiple backend workers and concurrent telemetry ingestion without duplicate incidents.

Do not introduce a distributed messaging system solely for this feature.

Use existing SignalR and background-worker infrastructure.

Keep notification processing independent of whether the frontend is open.

---

# PHASE 14 — Future Automation Integration

Design the incident system so future automations can react to events.

Example:

When:

- Grid outage is Critical.
- Battery SOC < 20%.

Potential future action:

- Shed selected nonessential loads.

Do not implement automatic power switching in this task.

Do not execute device commands merely because a notification rule matches.

Keep alert evaluation and device command execution separate.

Document safe future integration points.

---

# PHASE 15 — Delivery and Documentation

Implement the advanced rule engine as part of the centralized notification subsystem.

Retain all previously specified requirements for:

- SignalR.
- In-app notification bell.
- Critical warning banners.
- Browser notifications.
- Background tab awareness.
- Notification preferences.
- Notification history.
- Mobile push extension architecture.
- User/property authorization.
- Reliability and security.

Update architecture documentation and ADRs where appropriate.

Document:

- Rule definition format.
- Condition evaluation.
- Unknown/stale behavior.
- Correlation.
- Severity escalation.
- Recovery.
- Notification delivery.
- Rule editor behavior.
- Supported and unsupported signal types.
- Operational troubleshooting.

Archive the prompt using the next available numbering convention.

Complete implementation, tests, builds and documentation before committing.

# Constraints

- Do not modify or reset existing Configuration Vault credentials.
- Do not modify production or development database records as part of implementation or testing.
- Preserve existing OAuth flow behavior except for the requested safety improvements.
- Follow project architecture and UI conventions.
- Report any potentially destructive existing backend behavior before changing it.

Implement the change, add tests, and create a local commit. Do not push.

# Final Report

Provide:

1. Architecture overview.
2. Rule engine implementation.
3. Condition model and operators.
4. Composite rule behavior.
5. Correlation and incident escalation.
6. MySQL migrations.
7. Supported event sources.
8. Browser notification functionality.
9. Rule editor and incident history UI.
10. Future mobile integration points.
11. Test results.
12. Performance and reliability considerations.
13. Deployment and migration instructions.
14. Known limitations.
15. Local commit hash.

Explicitly confirm that existing credentials, OAuth behavior and real database records were not modified and that nothing was pushed.

## Follow-up

The operator was asked how to handle the missing earlier specification and chose: "Use Phase 15 list (Recommended)". The Phase 15 list is therefore the inherited requirements: SignalR, in-app notification bell, critical warning banners, browser notifications, background-tab awareness, notification preferences, notification history, mobile push extension architecture, user/property authorization, reliability and security.

## Execution summary

- **Architecture:** signals (`latest_readings`, `device_state`, `device_alerts`) → structured rules → correlated incidents → notifications with recipients and per-channel deliveries. See [alert rules, incidents and notifications](../architecture/17-alerts-and-notifications.md) and [ADR 0008](../architecture/adr/0008-alert-rules-and-incidents.md).
- **Backend:**
  - Domain: pure condition model, validation, three-valued evaluation, rule state machine, incident planner and preference routing.
  - Application: contracts, options and ports (`IAlertTrigger`, `INotificationChannel`, `IAlertLiveNotifier`).
  - Infrastructure: `AlertSignalReader`, `AlertRuleStore`, `AlertTemplates`, `AlertEvaluator`, `NotificationStore` and `NotificationDispatcher`.
  - API: `AlertEndpoints`, `AlertWorker`, SignalR live notifier and browser channel.
  - `TelemetryWriter` wakes evaluation after commits.
- **Migration 021:** 11 additive tables, optional for readiness, no grant change.
- **Frontend:** Automations → Alert rules (list, filters, visual editor, preview, templates, grid outage setup); Alerts (incidents with timeline and acknowledgment, history, notification settings); header bell; Critical banner; background-tab browser notifications.
- **Verification:**
  - Backend Release build with zero warnings.
  - Backend with disposable MySQL 8.0.46 fixtures: 353 passed, 11 skipped, twice in a row.
  - Backend without fixtures: 307 passed, 57 skipped.
  - `--migrate` smoke on a fresh disposable schema with a least-privilege migration account.
  - Frontend build; Vitest 123 passed.
- **Not touched:** live and development databases, vault credentials and OAuth.
- **Files:** `src/backend/*/Alerts.cs`, `src/backend/HomeAutomation.Infrastructure/MySql/Alert*.cs` and `NotificationStore.cs`, `src/backend/HomeAutomation.Api/Alert*.cs`, `database/migrations/021_alerts_and_notifications.sql`, `src/frontend/home-automation-web/src/shared/alerts.ts`, `src/frontend/home-automation-web/src/features/alerts/`, tests `AlertRuleEngineTests.cs`, `AlertMySqlTests.cs`, `AlertApiTests.cs` and `Alerts.test.tsx`, and the documentation listed in the handoff.
