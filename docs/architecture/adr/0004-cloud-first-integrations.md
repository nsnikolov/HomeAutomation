# ADR 0004 — Supported cloud adapters first

**Status** Proposed

## Context

The property already uses ShinePhone and ONECTA. Supported control paths and model-specific data access remain to be verified. A local-first rollout would require site hardware and protocol validation upfront.

## Decision

Start with official or explicitly supported cloud adapters and simulators. Keep provider-neutral ports and record per-metric source and quality. Keep solar monitoring read-only. Allow a future approved local adapter or agent to supplement or replace selected observations.

## Consequences

The simulated vertical slice can ship while access is pending. Cloud functionality depends on account access, quota, Internet availability and provider latency. If those prevent trustworthy outage evidence, use a validated local signal before enabling the dependent rule. Local telemetry alone cannot make cloud-only actuators available offline. [Integration validation](../05-integrations.md)
