# ADR 0003 — MySQL and Dapper

**Status** Proposed

## Context

Inventory and authorization need relational integrity. Telemetry range scans, rollups and command claims need explicit SQL and transaction control. The selected database is MySQL 8.4 LTS.

## Decision

Use InnoDB with Dapper and MySqlConnector. Keep use-case-specific SQL, parameterization, and explicit transactions. Use version-controlled SQL migrations with a separate deploy-time runner. Treat data contracts and migrations as tested application boundaries.

## Consequences

The team can tune queries and locking behavior directly. Mapping, migration verification and SQL maintenance are explicit responsibilities. A full ORM such as EF could simplify some write models but would add a second persistence style with little initial benefit. [Data model](../04-data-model.md)
