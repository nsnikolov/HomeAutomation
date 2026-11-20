# ADR 0007 — Device capabilities and multiple provider bindings

**Status** Proposed

## Context

One physical device can combine a switch, meter and sensors. A relay can have independently addressed channels. Cloud, gateway and LAN discovery can report the same device with different identifiers and accessible features.

## Decision

Use a property-owned physical device with stable application endpoints for its default target and any channels. Persist typed capability metadata per endpoint. Define supported, unsupported and unknown states, separate read and write access, and validate typed operations against binding-specific constraints and site policy. Keep provider DTOs and field mappings in independent adapters. Assign each external identity to a confirmed physical record, select canonical read sources per metric, and route each command attempt through one eligible binding under the existing physical-device dispatch lock.

## Alternatives and consequences

Capability interfaces on every device would provide compile-time behavior at the cost of adapter and persistence complexity for each combination. An unrestricted state dictionary would be easy to ingest but would hide units, access rules and command contracts from validation. The typed catalog supports Dapper filters and common UI panels with bounded metadata. Each adapter must maintain mappings, and administrators must resolve uncertain identity matches. Cloud-first rollout and durable automation remain governed by the existing decisions. [Data model](../04-data-model.md) · [Integration design](../05-integrations.md) · [ADR 0004](0004-cloud-first-integrations.md) · [ADR 0005](0005-automation-execution.md)
