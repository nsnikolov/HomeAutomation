# ADR 0001 — Modular monolith

**Status** Proposed

## Context

One property and a few users need reliable independent polling, rules and control. Telemetry updates, rule work and command recording benefit from shared transactions. The team will operate the same product in cloud and self-hosted environments.

## Decision

Run one .NET API and hosted-worker process, using application modules with explicit responsibilities, one MySQL database, and separate projects for Domain, Application, Infrastructure and Api. Keep provider adapters in Infrastructure initially. Use direct application calls and MySQL-backed work for durable operations.

## Consequences

The initial design has one deployment and fewer network failure paths. Workloads share process lifecycle and scaling, so bound worker concurrency and isolate expected provider failures. Revisit separate workers or services when measured throughput, fault isolation or independent release ownership requires them. Microservices would add network and operational coordination before those needs exist. [Backend architecture](../02-backend-architecture.md)
