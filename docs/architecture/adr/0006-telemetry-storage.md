# ADR 0006 — Typed telemetry and bounded retention

**Status** Accepted for hourly rollups (Milestone 7A). Retention remains proposed.

## Context

The first sites need both time-series charts and auditable state transitions. Provider observations may be late, duplicated or shared across devices. Detailed samples have greater storage cost than summarized history.

## Decision

Use indexed typed MySQL series keyed by subject, metric and source. Maintain a latest-state projection and retain meaningful transitions, sampled detail, and coverage-aware hourly rollups. Keep the initial schema unpartitioned.

Milestone 7A implements UTC hourly rollups per physical series with bounded-hold coverage, and a materialized-range state per series. It computes local calendar days from hourly rows plus raw partial hours instead of persisting a daily table. Measured counter energy and estimated power energy stay separate. Retention is deferred. The planning defaults of 30 days of detail and 12 months of hourly rollups apply only after storage growth has been measured.

## Consequences

The first deployment uses one database and can trace canonical totals to their sources. Exact local days need raw samples for partial-hour edges, which a future retention policy must keep or replace with explicit partial buckets. Storing every poll retains many repeated observations. A dedicated time-series store or partitioning could help at higher volume but introduces another store or schema constraints. Review after measured load and growth. [Telemetry lifecycle](../04-data-model.md) · [Historical telemetry](../10-historical-telemetry.md)
