# ADR 0006 — Typed telemetry and bounded retention

**Status** Proposed

## Context

The first sites need both time-series charts and auditable state transitions. Provider observations may be late, duplicated or shared across devices. Detailed samples have greater storage cost than summarized history.

## Decision

Use indexed typed MySQL series keyed by subject, metric and source. Maintain a latest-state projection and retain meaningful transitions, sampled detail, and coverage-aware hourly rollups. Start with configurable 30-day detail and 12-month hourly defaults. Keep the initial schema unpartitioned.

## Consequences

The first deployment uses one database and can trace canonical totals to their sources. Retention, rollup correctness and gaps require explicit policies. Storing every poll would retain many repeated observations. A dedicated time-series store or partitioning could help at higher volume but introduces another store or schema constraints. Review after measured load and growth. [Telemetry lifecycle](../04-data-model.md)
