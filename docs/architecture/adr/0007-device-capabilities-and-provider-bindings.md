# ADR 0007 — Device capabilities and multiple provider bindings

**Status** Proposed

## Context

One physical device can combine a switch, meter and sensors. A relay can have independently addressed channels. Cloud, gateway and LAN discovery can report the same device with different identifiers and accessible features.

## Decision

Use a property-owned physical device with stable application endpoints for its default target and any channels. Persist typed capability metadata per endpoint. Define supported, unsupported and unknown states, separate read and write access, and validate typed operations against binding-specific constraints and site policy. Keep provider DTOs and field mappings in independent adapters. Assign each external identity to a confirmed physical record, select canonical read sources per metric, and route each command attempt through one eligible binding under the existing physical-device dispatch lock.

## Alternatives and consequences

Capability interfaces on every device would provide compile-time behavior at the cost of adapter and persistence complexity for each combination. An unrestricted state dictionary would be easy to ingest but would hide units, access rules and command contracts from validation. The typed catalog supports Dapper filters and common UI panels with bounded metadata. Each adapter must maintain mappings, and administrators must resolve uncertain identity matches. Cloud-first rollout and durable automation remain governed by the existing decisions. [Data model](../04-data-model.md) · [Integration design](../05-integrations.md) · [ADR 0004](0004-cloud-first-integrations.md) · [ADR 0005](0005-automation-execution.md)

## Addendum — Logical devices (Milestone 7B)

**Status** Accepted.

A logical device gives a monitored role a stable identity independent of hardware. Its channels are assigned to physical devices over half-open intervals, and metrics resolve by canonical `Metric` across the assigned device's endpoints. An endpoint is pinned only when one measurement capability repeats across endpoints. Assignments never move or rewrite physical history: series, samples and rollups stay owned by the physical device, and logical history uses only observations inside each assignment. Changes are serialized per property, validated for interval overlap in the application, and audited. The alternatives were endpoint-level assignment, which cannot follow Tuya's per-data-point endpoints to a single-endpoint eWeLink sensor, and re-keying series to logical identities, which would rewrite history and blur provenance. [Logical devices](../11-logical-devices.md)

## Addendum — Automatic logical identities (Milestone 7E)

**Status** Accepted.

- **Decision.** Newly discovered hardware of a property that already uses logical devices gets one automatic logical identity. Provisioning is a provider-neutral sweep over the accepted inventory, kept out of ingestion. It runs at most once per physical device, enforced by the property row lock and the unique origin key. Hardware that has ever been assigned is never provisioned again.
- **Replacement remains an explicit user decision.** An automatic identity is released only when the user names it, at an explicit time and with confirmation when it would keep readings. Raw telemetry, rollups and dashboard bindings are not rewritten.
- **Consequence.** Users decide afterwards whether new hardware is new equipment or a replacement. The trade-off is a short-lived duplicate identity until they resolve it.
- See [logical device management](../14-logical-device-management.md).

## Addendum — Moving devices between properties

**Status** Accepted.

- **Decision.** Telemetry stays owned by the property where it was recorded. Moving a device retires its source row and opens (or reactivates) a destination row with the same inventory key. Endpoints, capabilities, binding and endpoint bindings are copied, and the provider row moves. Series and samples are never re-keyed.
- **Alternatives.** Re-keying series to the new property would rewrite history and break the composite property foreign keys. Hard-deleting properties or devices would destroy history and could orphan vault credentials, so properties are soft-deleted.
- **Consequence.** One physical device can have several device rows over time, at most one current. Readers exclude retired rows, and history before the move stays in the source property. Growatt hardware follows the site configuration and does not move this way.
- See [property, room and device management](../16-property-room-device-management.md).
