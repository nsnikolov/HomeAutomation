# ADR 0005 — Durable backend automation

**Status** Proposed

## Context

Rules must run without a browser and recover from restarts. Provider writes may succeed before the caller sees a response, and multiple rules and operators can target the same device.

## Decision

Use a deterministic application evaluator, durable MySQL work and an independently dispatched command ledger. Evaluate per property and serialize writes per device. Share arbitration, safety checks and reconciliation for manual and automated actions. Keep dry-run side effects confined to evaluation records.

## Consequences

Explicit checkpoints and command state support replay and forensics. A cloud write cannot be committed atomically with its local database record, so ambiguous outcomes require reconciliation. An in-memory scheduler could lose state during shutdown, and a dedicated workflow engine would add operational cost. Revisit a separate worker or workflow engine when execution volume or orchestration complexity warrants it. [Automation engine](../06-automation-engine.md)
