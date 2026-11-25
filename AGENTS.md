# Home Automation

Home Automation monitors property devices, including Growatt solar inverters and battery storage, Daikin ONECTA HVAC, eWeLink devices, and Smart Life / Tuya sensors. The stack is .NET 10 and C#, ASP.NET Core and SignalR, MySQL and Dapper, and React / TypeScript with Vite.

## Start a session

At the beginning of every new development session or task:

1. Read [docs/AI_HANDOFF.md](docs/AI_HANDOFF.md). If it is already in the current context, use that copy unless the file has changed.
2. Inspect Git status and recent commits.
3. Review relevant architecture documents and ADRs from the [architecture guide](docs/architecture/01-system-overview.md).
4. Check whether the requested functionality already exists.
5. Follow the project's architecture, security and safety constraints.

Open only the relevant sections of longer documents. Use the [README](README.md) for setup, the [integration guides](docs/integrations/) for provider behavior, and the [prompt history](docs/prompts/README.md) for prior requests and verification records.

## Architecture and development

- Preserve the modular monolith. Domain and Application own device capabilities and use cases. Infrastructure implements provider clients and Dapper/MySQL persistence. The API composes them, enforces access, and publishes SignalR notifications. Use the existing provider-neutral contracts and shared read models.
- Backend projects live in [src/backend/](src/backend/). The React SPA and Vitest tests live in [src/frontend/home-automation-web/](src/frontend/home-automation-web/). Backend xUnit projects are in [tests/](tests/). Versioned SQL lives in [database/migrations/](database/migrations/). [docker/](docker/) contains deployment packaging.
- For backend verification from the root, run `dotnet build HomeAutomation.slnx -c Release` and `dotnet test HomeAutomation.slnx -c Release --no-build`. From the frontend directory on Windows, run `npm.cmd ci` when dependencies need restoring, then `npm.cmd run build` and `npm.cmd test`. Use isolated MySQL fixtures for opt-in database tests.
- Keep applied SQL files immutable and add new numbered migrations for schema changes. The explicit `dotnet run --project src/backend/HomeAutomation.Api -- --migrate` command uses a scoped migration account. It serializes changes and records each successful embedded script and its SHA-256 checksum in `schema_migrations`. Normal startup verifies readiness with the scoped runtime account. See [database setup](README.md#apply-migrations-and-provision-a-viewer) for credential setup and existing-schema repair guidance. Apply live migrations only with explicit approval. Permission changes require a separate, explicit administrative action.

- Follow established repository conventions as the solution evolves. Keep changes scoped to the requested behavior and preserve existing configuration and device behavior unless the change calls for an update.
- Confirm provider-specific capabilities, units, and command semantics against the relevant device or API documentation. Keep credentials and tokens out of source control, fixtures, and logs.
- Use simulated devices for routine development and verification. Once a solution exists, run its relevant `dotnet build` and `dotnet test` commands and report any verification gaps.
- Use the task-specific skills in `.agents/skills/` for C# development, external APIs, automation rules, tests, and reviews.

## Security, monitoring and control

- Preserve cookie authentication, antiforgery protection, and server-side property membership checks for API operations and SignalR subscriptions. Viewers monitor. Owner, Admin, and Operator roles can configure the property dashboard. Control routes also enforce provider-specific permissions and eligibility.
- The monitoring dashboard displays telemetry and authorized navigation. Keep equipment actions on the provider pages and keep browser-only widget preferences separate from the shared property layout.
- Preserve provider request budgets, polling intervals, cooldowns and backoff. Add mappings only for verified provider capabilities and units. Preserve source time, observation provenance and freshness. Confirm per-device or installation scope before aggregating telemetry.
- Growatt and Tuya remain read-only, as do Daikin Altherma and eWeLink THR320D. Preserve explicit enablement, device eligibility, protected relay confirmations, command audits and observed readback for supported manual controls. A provider acknowledgement only confirms acceptance. Reconcile ambiguous outcomes before sending another command. Automated tests use simulated or mocked devices. Any real equipment command requires explicit approval.
- Keep credentials, OAuth tokens, session details and private connection strings out of Git, fixtures, logs and the handoff. Use development User Secrets or approved deployment secret configuration. Keep the documented local listeners on loopback and MySQL off the public network.

## Finish a task

After meaningful development work:

1. Update [docs/AI_HANDOFF.md](docs/AI_HANDOFF.md) with verified changes, remaining work and relevant verification results.
2. Keep the handoff concise and current.
3. Archive substantial prompts under `docs/prompts`.
4. Commit documentation with implementation changes where appropriate.
5. Never push without explicit user authorization.

Inspect the staged diff before committing. Preserve unrelated working-tree changes. Use a descriptive commit subject and report the checks that actually ran, including skips and external dependencies.

## Project prompt history

- Before implementing a substantial development request, save its title, date and complete original text, including relevant attachments, under `docs/prompts/`. Use the next sequential filename in the form `0001-short-description.md`. Preserve wording and formatting. Replace passwords, API tokens, credentials and other secrets with `[REDACTED]` before writing the entry.
- Update the [chronological prompt index](docs/prompts/README.md) when adding an entry. Keep each saved entry and its original text permanently. Archive corrections and follow-ups to saved prompts as new entries. Import earlier prompts only from their original text.
- After completion, append a short execution summary and references to the relevant commits or changed files below the saved prompt. Commit the entry and index alongside the related changes.

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

Use this workflow when Graphify is already available. Continue with ordinary repository navigation if it is unavailable.

When the user types `/graphify`, use the installed graphify skill or instructions before doing anything else.

Rules:
- When graphify-out/graph.json exists, run `graphify query "<question>"` before your first grep, search or source-file read for a codebase question, and put this rule in every subagent prompt that explores code. Read raw files only for lines the graph or an earlier read already located. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. When the answer is under a Markdown heading, `graphify section "<heading>"` prints that section's text, so the .md need not be opened. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- Dirty graphify-out/ files are expected after hooks or incremental updates; dirty graph files are not a reason to skip graphify. Only skip graphify if the task is about stale or incorrect graph output, or the user explicitly says not to use it.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
- A committed graphify-out/graph.json is rewritten whole on every build, so a large diff to it in a pull request is expected. Do not flag it as churn or suggest skipping the graph rebuild or its hooks.
