# 0001 — Implement Milestone 1 — Home Automation Application Foundation

**Date** 2026-11-21

## Original prompt

# Implement Milestone 1 — Home Automation Application Foundation

Act as a Senior Full-Stack .NET and React Engineer.

Review the existing architecture documents under `docs/architecture/`, all relevant ADRs, AGENTS.md, and applicable project-specific Codex Skills before implementation.

Implement Milestone 1 according to the approved architecture.

## Technology Stack

- .NET 10 / ASP.NET Core Web API
- React + TypeScript + Vite
- Tailwind CSS + shadcn/ui
- TanStack Query
- Recharts
- SignalR
- MySQL 8.4 LTS
- Dapper + MySqlConnector
- Docker Compose
- xUnit, Vitest and React Testing Library

Do not introduce Entity Framework or unnecessary additional frameworks.

## 1. Backend Foundation

Create the .NET solution with the projects defined in the architecture:

- HomeAutomation.Api
- HomeAutomation.Application
- HomeAutomation.Domain
- HomeAutomation.Infrastructure

Implement proper project references and dependency boundaries.

Set up dependency injection, structured logging, configuration validation, exception handling, and health checks.

Create an initial API structure with versioned routes.

Use nullable reference types, async/await, cancellation tokens, and TimeProvider where appropriate.

## 2. Database Foundation

Create a MySQL 8.4 database configuration.

Implement version-controlled SQL migrations.

Create the initial schema according to `04-data-model.md`, focusing on the entities necessary for Milestone 1.

Include at least:

- Properties
- Rooms
- Devices
- Device endpoints
- Device capabilities
- Device state
- Telemetry measurements
- Provider bindings

Introduce additional tables only when required by the approved architecture or initial functionality.

Use Dapper and MySqlConnector for all database access.

Ensure migrations are repeatable and safe for a fresh deployment.

Add appropriate indexes and foreign keys.

## 3. Simulated Device Integration

Implement a simulator provider that produces realistic but deterministic data for:

- Two Growatt SPF 5000 ES inverters operating in parallel.
- One shared battery.
- One Daikin air conditioner.
- One Sonoff power-monitoring smart switch.
- One temperature sensor.

Use the same application interfaces intended for real device integrations.

Support:

- Device discovery.
- Device states.
- Telemetry ingestion.
- Online/offline conditions.
- Stale data.
- Simulated device faults.

Make simulator configuration explicit and controllable.

Do not connect to real provider APIs yet.

## 4. Background Telemetry Processing

Implement a hosted background service for simulated telemetry ingestion.

Requirements:

- Configurable polling interval.
- Cancellation support.
- No overlapping polling cycles.
- Proper scoped dependency management.
- Structured logging.
- Graceful shutdown.
- Recovery from transient failures.
- Persistence of telemetry in MySQL.

Ensure telemetry ingestion continues when the browser is closed.

Do not implement the automation engine yet.

## 5. API and SignalR

Implement initial REST endpoints for:

- Properties and rooms.
- Device listing.
- Device details.
- Device capabilities.
- Latest device states.
- Historical telemetry.
- System health.

Use SignalR for pushing updated device states to connected clients.

Handle disconnected clients gracefully.

Do not expose provider credentials or infrastructure-specific models.

## 6. React Frontend

Create the React + TypeScript + Vite application.

Use:

- Tailwind CSS
- shadcn/ui
- TanStack Query
- Recharts

Implement a responsive application shell with:

- Dashboard
- Devices
- Energy
- Automations (placeholder only)
- Settings (placeholder only)

Dashboard should show:

- PV production.
- Battery SOC.
- Grid availability.
- Household consumption.
- Current device status.
- Recent device alerts.
- Selected temperature measurements.

Devices page should group devices by property and room.

Show capabilities using reusable components.

Energy page should include basic historical charts.

Use TanStack Query for server state and SignalR for live updates.

Distinguish online, offline, stale, and fault states visually.

Do not implement real device control commands yet.

## 7. Docker and Local Development

Create Dockerfiles and Docker Compose configuration.

The complete application must be startable with:

docker compose up -d --build

Include:

- Backend
- MySQL 8.4
- Frontend, either served through the backend or packaged according to the approved architecture

Support local development using VS Code.

Document environment variables, development commands, database initialization, and debugging.

Do not commit secrets or real credentials.

## 8. Testing

Create backend and frontend test projects.

Include tests covering:

- Domain model validation.
- Device capability handling.
- Telemetry freshness.
- Correct aggregation of inverter measurements.
- Avoidance of double-counted energy.
- Simulated telemetry generation.
- Basic API responses.
- Basic frontend rendering.

Use deterministic tests without external cloud services.

## 9. Quality Requirements

- Follow established repository conventions.
- Keep implementation proportional to Milestone 1.
- Avoid premature generic frameworks.
- Do not implement Growatt, Daikin, eWeLink or Tuya cloud connections yet.
- Do not implement live electrical switching.
- Do not implement automation rule execution.
- Keep device control disabled for simulated and real integrations at this stage.
- Ensure simulator-only functionality cannot accidentally target real devices.
- Keep the architecture ready for a future autonomous local agent capable of executing a limited set of safety-approved rules without Internet connectivity.
- Do not implement the local agent now.

## 10. Completion Criteria

Before considering the milestone complete:

1. Restore all .NET and npm dependencies.
2. Build the backend successfully.
3. Build the React frontend successfully.
4. Run backend tests.
5. Run frontend tests.
6. Validate SQL migrations.
7. Start Docker Compose if Docker is available.
8. Verify health checks and basic API endpoints.
9. Confirm simulated telemetry reaches the dashboard.
10. Confirm telemetry ingestion continues independently of the browser.

If any verification cannot be performed, explain exactly why.

## 11. Final Report

Provide:

- Implemented features.
- Solution and folder structure.
- Database schema and migrations.
- Commands to run locally.
- Test results.
- Known limitations.
- Deviations from approved architecture.
- Recommended next milestone.

Implement the milestone incrementally and verify each major component before proceeding.

Do not stop after generating scaffolding. Deliver a working, testable foundation.

---

## Project Prompt History — Mandatory Repository Convention

From now on, maintain a permanent history of all substantial development prompts issued to Codex for this repository.



Create the directory:



`docs/prompts/`



Rules:

1. Save each substantial user-provided development prompt as a separate Markdown file before implementing it.
2. Use sequential filenames: `0001-short-description.md`, `0002-short-description.md`, etc.
3. Include the date, prompt title, and complete original prompt text.
4. Preserve the original instructions without summarizing or rewriting them.
5. Add a short execution summary and references to relevant commits or changed files after completion.
6. Maintain `docs/prompts/README.md` as a chronological index.
7. Never overwrite or delete previous prompts. Corrections or follow-up prompts must be saved as new files.
8. Do not include passwords, API tokens, credentials, or other secrets in prompt history. Replace sensitive values with `[REDACTED]`.
9. Do not invent or reconstruct previous prompts from memory. Start the archive with the current Milestone 1A prompt. Earlier prompts may be imported later from their original text.

Update the repository's `AGENTS.md` with this convention so future Codex sessions follow it automatically.



For the current task:

- Create `docs/prompts/0001-milestone-1a-foundation.md`.
- Save the complete Milestone 1A prompt, including this prompt-history instruction.
- Then proceed with implementing Milestone 1A.

This prompt history is part of the project's documentation and must be committed to Git alongside the related changes.

## Execution summary

Implemented the read-only Milestone 1 foundation with four .NET 10 projects, property-scoped viewer sign-in, two versioned MySQL migrations, six simulated devices, background telemetry, a SignalR dashboard, and local-development tooling.

The Release build passed with 13 xUnit tests. A clean npm restore, the production frontend build, and three Vitest tests passed. On isolated MySQL 8.4.13, fresh and repeat migrations succeeded. A browser smoke test verified sign-in, dashboard readings and history, live updates, room grouping, read-only capabilities, and continued ingestion after closing Chrome. The worker recovered after the isolated database restarted. Docker was unavailable for a Compose launch on this machine.

- [Solution and local development](../../README.md)
- [Solution file](../../HomeAutomation.slnx)
- [Foundation schema](../../database/migrations/001_foundation.sql) and [simulated property seed](../../database/migrations/002_demo_property.sql)
- [Compose configuration](../../compose.yaml)
