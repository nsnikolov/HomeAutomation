# HomeAutomation — Cross-Agent Context, Documentation and Automatic Handoff

Date: 2026-11-25

## Original prompt

# HomeAutomation — Cross-Agent Context, Documentation and Automatic Handoff

Prepare the HomeAutomation repository for seamless development between OpenAI Codex and Anthropic Claude Code.

The goal is to allow either agent to start a new session, understand the current project state automatically, and continue development without requiring the user to repeatedly explain the architecture, previous decisions, completed work or outstanding issues.

This is a documentation and agent-configuration task. Do not implement new application features.

## 1. Inspect the existing project

Review the repository and existing documentation, including:

- Root AGENTS.md and CLAUDE.md, if present.
- README.md.
- Architecture documents and ADRs.
- Archived prompts under docs/prompts.
- Current Git status and recent commits.
- Backend, frontend and database structure.
- Existing integrations and Dashboard implementation.
- Local development and deployment instructions.

Use Graphify for targeted codebase navigation if already available and useful. Do not install or troubleshoot Graphify.

Reuse existing documentation and avoid unnecessary duplication.

## 2. Shared instructions — AGENTS.md

Create or update the repository-root AGENTS.md.

This must be the main, agent-neutral source of development instructions.

Preserve useful existing instructions and document:

- Project purpose, stack and repository organization.
- Architectural constraints and coding conventions.
- Build and testing commands.
- Database migration workflow, including automatic recording of migration checksums in schema_migrations.
- Authentication and property-scoped authorization.
- Provider polling, API limits and integration constraints.
- Read-only Dashboard behavior.
- Physical device-control safety.
- Security and secrets handling.
- Git workflow and commit conventions.
- Relevant documentation locations.

Include explicit startup instructions:

"At the beginning of every new development session or task:

1. Read docs/AI_HANDOFF.md.
2. Inspect Git status and recent commits.
3. Review relevant architecture documents and ADRs.
4. Check whether the requested functionality already exists.
5. Follow the project's architecture, security and safety constraints."

Include completion instructions:

"After meaningful development work:

1. Update docs/AI_HANDOFF.md with verified changes, remaining work and relevant verification results.
2. Keep the handoff concise and current.
3. Archive substantial prompts under docs/prompts.
4. Commit documentation with implementation changes where appropriate.
5. Never push without explicit user authorization."

Avoid rereading large documents unnecessarily within the same session.

## 3. Claude Code — CLAUDE.md

Create or update the repository-root CLAUDE.md.

Use Claude Code's supported import syntax to reference the shared instructions:

@AGENTS.md
@docs/AI_HANDOFF.md

Add only necessary Claude-specific instructions.

Do not duplicate the full shared instructions.

Ensure the document is compatible with the current Claude Code conventions.

## 4. Current project state — docs/AI_HANDOFF.md

Create or update docs/AI_HANDOFF.md.

Include:

### Current implementation
- Completed milestones and major functionality.
- Working integrations: Growatt, Daikin, eWeLink and Tuya.
- Dashboard capabilities, configuration and persistence.
- Authentication and property permissions.
- Recent relevant commits.

### Architecture
- Backend/frontend stack.
- Database and migration mechanism.
- Important components and boundaries.
- Telemetry storage and SignalR.
- Provider integration design.

### Development environment
- Windows development workflow.
- Backend startup and restart commands.
- Log locations.
- Frontend build/test commands.
- Database administration and migration commands, verified against the repository.

### Operational constraints
- Provider API limits and polling intervals.
- Known integration limitations.
- Physical device safety rules.
- Remote access and networking considerations, where documented.

### Current status and next steps
- Outstanding tasks.
- Confirmed unresolved issues.
- Workarounds.
- Current branch, latest relevant commit and uncommitted changes.

### Verification
- Last successful build and test results.
- Skipped tests and external dependencies.
- Features verified against real devices versus simulations.

Use only verified repository information. Clearly distinguish confirmed functionality, known limitations and future plans.

Do not include passwords, API keys, tokens or private connection strings.

Keep the handoff concise, ideally around 100–200 lines.

## 5. Automatic loading and cross-agent workflow

Ensure:

- Codex automatically receives AGENTS.md through its native project-instruction mechanism.
- AGENTS.md explicitly requires reading docs/AI_HANDOFF.md.
- Claude Code automatically loads CLAUDE.md.
- CLAUDE.md imports the shared rules and handoff context.
- Both agents follow consistent rules without conflicting instructions.
- Both agents update AI_HANDOFF.md after meaningful development changes.

Do not introduce scripts that automatically launch agents or consume API tokens.

Do not create unnecessary duplicate documentation.

## 6. Safety and scope

Important:

- Never execute real physical-device commands during automated tests.
- Never run migrations against the live database without explicit approval.
- Never change live database permissions automatically.
- Never commit credentials, tokens or secrets.
- Never push Git changes without explicit authorization.
- Avoid unrelated refactoring.
- Preserve working integrations.
- Do not modify application source code or database schemas in this task.

## 7. Verification and completion

Verify:

- AGENTS.md follows Codex conventions.
- CLAUDE.md follows Claude Code conventions and import syntax.
- The automatic context-loading paths are valid.
- All documentation references point to existing files.
- The handoff reflects the actual current Git state.
- No existing useful instructions were lost.
- No secrets are included.

If Claude Code is installed, perform a lightweight, non-mutating check that it recognizes the project instructions. Otherwise, verify the configuration statically; do not install Claude Code.

Report:
- Files created or updated.
- How automatic context loading works for both agents.
- Any limitations.
- How to continue after switching agents.

Archive this prompt under docs/prompts.

Commit locally without pushing.

## Execution summary

- Updated the [shared development instructions](../../AGENTS.md) with startup, completion, safety and verification guidance. Preserved the existing prompt archive and local Graphify conventions.
- Added [Claude Code imports](../../CLAUDE.md) and the [project handoff](../AI_HANDOFF.md) with shipped behavior, historical verification, current limitations and instructions for switching agents. Linked both from the existing setup guide.
- Checked the instruction-loading conventions against the official Codex and Claude Code documentation. Validated imports and internal links statically. Claude Code was unavailable locally. This change was documentation-only.
