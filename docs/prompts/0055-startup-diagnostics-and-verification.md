# HomeAutomation — Milestone 6C: Startup Diagnostics and Configuration Verification

Date: 2026-11-25

## Original prompt

# HomeAutomation — Milestone 6C: Startup Diagnostics and Configuration Verification

Implement a focused operational improvement for HomeAutomation.



Use Medium reasoning. Keep the scope small and suitable for a limited Codex usage budget.

## Context

- Read AGENTS.md and docs/AI_HANDOFF.md.
- Inspect recent commits and Git status.
- Configuration Vault milestones 6A, 6A.1 and 6B are implemented.
- Provider cutover has been performed manually for Growatt, Tuya, Daikin and eWeLink.
- The current application must remain operational.

## 1. Improve backend restart diagnostics

Review the existing restart-backend.ps1 script.



Improve error handling when the backend fails to start.



Requirements:

- Detect premature process termination.
- Display the actual exit code when available.
- Report relevant recent startup errors from stdout/stderr logs.
- Distinguish port conflicts, build failures, missing configuration and invalid Vault profiles when evidence supports the diagnosis.
- Avoid exposing credentials or sensitive configuration values.
- Preserve existing behavior when startup succeeds.
- Avoid killing unrelated processes.

Do not introduce new deployment infrastructure.

## 2. Provider configuration startup logging

Ensure that backend startup provides one clear structured summary per provider:

- Provider name.
- Selected configuration source: Legacy or Vault.
- Profile scope: Global or Property ID.
- Configuration validation status.

Never log secrets or complete configuration objects.



Reuse existing configuration loading and validation mechanisms.



Do not introduce extra provider API requests.

## 3. Read-only verification command

Add a simple PowerShell script:



`verify-provider-configuration.ps1`



It should:

- Check whether the local backend is running.
- Read relevant startup diagnostics.
- Report the configuration source for Growatt, Daikin, eWeLink and Tuya.
- Identify missing startup confirmation or obvious errors.
- Clearly distinguish confirmed states from unknown states.
- Return an appropriate process exit code.
- Never modify configuration or database records.
- Never perform provider imports, activation or rollback.
- Never send physical device commands.

Use the project's existing logging conventions.

## 4. Tests

Add focused tests where practical.



Verify:

- Startup logging correctly identifies provider sources.
- No secrets appear in diagnostics.
- Failed startup produces useful error reporting.
- Missing or incomplete logs are reported as unknown, not success.

Run relevant builds and tests.

## 5. Constraints

- No database migrations.
- No changes to provider authentication.
- No OAuth changes.
- No modifications to physical devices.
- No configuration source switching.
- No automatic deployment actions.
- No unrelated refactoring.
- Do not restart the currently running application during implementation.

## 6. Completion

- Update documentation and AI_HANDOFF.md.
- Archive this prompt under docs/prompts.
- Commit locally without pushing.
- Report changes, tests and any remaining issues.

If the task is completed early, stop. Do not expand the scope to consume remaining tokens.

## Execution summary

- Added structured provider startup event 6100 and [read-only verification](../../verify-provider-configuration.ps1). The [restart script](../../restart-backend.ps1) verifies ownership before stopping a listener and reports sanitized startup failures with the launcher exit code.
- Release build passed with zero warnings. Backend tests reported 172 passes and 12 opt-in skips. PowerShell fixtures and an isolated invalid Vault launch passed.
- The current backend returned HTTP 200 and its provider sources were Unknown because matching startup events were absent. It was not restarted. See [Windows verification and exit codes](../../README.md#verify-provider-startup-on-windows).
