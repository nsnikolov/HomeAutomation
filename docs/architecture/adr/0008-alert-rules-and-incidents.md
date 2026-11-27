# ADR 0008 — Structured alert rules, correlated incidents and channel-neutral notifications

**Status** Accepted

## Context

Owners need alerts for outages, low battery, temperatures and offline devices without a code change for every threshold or combination. One physical event, a grid outage, can move between severities as the battery drains and recovers. Repeated polls, late provider data, restarts and several workers must not create duplicate incidents or notification storms. Equipment control has its own safety model ([ADR 0005](0005-automation-execution.md)) and must not be triggered by an alert.

## Decision

- **Rule definitions.** Rules are structured JSON: AND/OR groups of typed conditions over stable signal references (device endpoint and metric, installation metric, connectivity, device event). They allow a fixed operator set per value type, explicit any/all/single aggregation, per-condition maximum reading age and recovery thresholds. They are validated on save and evaluated with three-valued logic. Nothing in a definition is executable.
- **Detection state.** It is persisted per rule, so restarts resume instead of reopening. Unknown cancels a pending activation but never confirms a recovery.
- **Incidents.** Rules that share a correlation family contribute to one incident per property. Its severity is the highest active contributor's. A generated active-key column with a unique index enforces one active incident per family, and a per-property lock row serializes evaluations.
- **Notifications.** They are created per incident event, with recipients and per-channel delivery attempts recorded separately from incident state. Optional channels implement one interface. The browser channel uses the existing SignalR hub; mobile push is a future channel.
- **Separation from control.** Alert evaluation is separate from command execution: no alert path reaches a provider client.

## Consequences

- **Benefits.** New alerts need only data. History is complete, and a rule can be deleted without losing it. Duplicate ingestion, replays and concurrent workers are safe by construction. The schema is additive and needs no new grant.
- **Costs.** Expressiveness is deliberately limited: no arithmetic, cross-metric math or free aggregation. Logical-device references and provider account status are not signal kinds yet. A rule tied to a moved device must be edited.
- **Notification channels.** Browser delivery is only observable while a page is open. Push and e-mail need a provider decision and a device-registration store.
- **Changes to the model.** Adding a signal kind or operator is a code change with validation and tests, by design.

See [alert rules, incidents and notifications](../17-alerts-and-notifications.md).
