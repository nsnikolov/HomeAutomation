# ADR 0004 — Supported cloud adapters first

**Status** Proposed

## Context

The property already uses ShinePhone and ONECTA. Supported control paths and model-specific data access remain to be verified. A local-first rollout would require site hardware and protocol validation upfront.

## Decision

Start with official or explicitly supported cloud adapters and simulators. Keep provider-neutral ports and record per-metric source and quality. Keep solar monitoring read-only. Allow a future approved local adapter or agent to supplement or replace selected observations.

## Consequences

The simulated vertical slice can ship while access is pending. Cloud functionality depends on account access, quota, Internet availability and provider latency. If those prevent trustworthy outage evidence, use a validated local signal before enabling the dependent rule. Local telemetry alone cannot make cloud-only actuators available offline. [Integration validation](../05-integrations.md)

## Addendum — Simulator is opt-in (2026-11-26)

**Status** Accepted.

- **Decision.** The simulator that carried the first vertical slice is now an opt-in development and test tool, off by default. It is never a fallback for missing provider data.
- **Stored data.** Simulator data is identified by provenance: the property's `simulator` connection, `simulator:` inventory keys and the `simulator` source key. It is hidden from every read while the simulator is off, and is deleted only by an explicit, property-scoped, dry-run-first maintenance command. See [simulator removal](../15-simulator-removal.md).
- **Consequence.** Pages show truthful empty or unavailable states until providers report.
