# HomeAutomation — Milestone 6A.1: Separate Secret and Non-Secret Configuration

Date: 2026-11-25

## Original prompt

# HomeAutomation — Milestone 6A.1: Separate Secret and Non-Secret Configuration

Refine the existing Configuration Vault implementation from Milestone 6A (commit `e470cf6`).

The goal is to encrypt only actual secrets using AES-256-GCM, while storing non-secret configuration values as ordinary database fields and displaying their current values in Settings.

This refinement must be completed before Phase 6B provider migration.

## 1. Project context

- Read `AGENTS.md` and `docs/AI_HANDOFF.md`.
- Review `docs/architecture/09-configuration-vault.md`.
- Inspect the existing Configuration Vault implementation, database schema, API contracts and frontend.
- Inspect Git status and recent commits.
- Preserve the existing shared Codex/Claude Code handoff process.

## 2. Configuration classification

Apply the following classification:

| Provider | Setting | Classification |
|---|---|---|
| Growatt | API Token | Secret |
| Daikin | Client ID | Non-secret |
| Daikin | Client Secret | Secret |
| eWeLink | App ID | Non-secret |
| eWeLink | App Secret | Secret |
| Tuya | Access ID | Non-secret |
| Tuya | Access Secret | Secret |
| Tuya | Linked User ID | Non-secret |
| All providers | Polling interval | Non-secret |

Other existing provider identifiers, flags and operational settings should also be classified according to their actual security requirements.

Do not assume an identifier is a secret merely because it was previously stored in an encrypted credential slot.

Existing OAuth access tokens, refresh tokens, account keys and encryption keys must remain protected. Do not change their existing storage mechanisms during this milestone.

## 3. Backend changes

Introduce an explicit distinction between secret and non-secret configuration fields.

Requirements:

- Encrypt only secret values using the existing AES-256-GCM implementation.
- Store non-secret settings as readable database values.
- Preserve property and global scoping.
- Preserve authorization and validation rules.
- Preserve transactional updates and optimistic concurrency.
- Preserve audit logging without recording secret values.
- Keep encryption keys outside the database.
- Maintain nonce uniqueness and key rotation for encrypted values.
- Do not expose secrets through API responses.

Use the simplest architecture consistent with the existing implementation.

Avoid unnecessary abstractions or unrelated refactoring.

## 4. Database compatibility

Migration `014_configuration_vault.sql` has already been applied to the development database.

Do not modify migration 014 or its checksum.

If schema changes are required, create a new versioned migration (`015_...sql`) using the existing migration framework.

Handle previously staged encrypted identifiers safely:

- Detect existing encrypted non-secret fields.
- Support a controlled conversion to plaintext storage.
- Preserve their original values.
- Avoid data loss or accidental credential removal.
- Keep the operation idempotent where practical.
- Do not activate provider integrations.
- Do not modify live database contents automatically.

If the existing schema supports this change without a new migration, document why.

## 5. Settings UI

Update the Settings interface.

For non-secret fields:

- Display their current saved values.
- Allow authorized users to edit them normally.
- Preserve values when other settings are modified.
- Apply normal validation.

For secret fields:

- Keep them write-only.
- Show Configured / Not configured.
- Never return the existing secret value to the browser.
- Allow explicit replacement or removal.
- Leaving a secret input blank must preserve its existing value.

Maintain the existing visual design and permission model.

## 6. Security

Ensure that non-secret identifiers are still protected by normal authentication, authorization and property isolation.

Do not publish configuration values through unauthenticated endpoints.

Ensure secrets never appear in logs, audit records, API responses or browser state.

Preserve existing encryption and rotation behavior for genuine secrets.

## 7. Tests

Add or update tests verifying:

- Secrets are encrypted before database persistence.
- Non-secret identifiers are stored and retrieved without encryption.
- Non-secret values appear correctly in Settings.
- Secret values are never returned by APIs.
- Existing encrypted non-secret values remain recoverable.
- Global and property-specific configuration isolation.
- Secret replacement and explicit removal.
- Concurrent updates and audit logging.
- Existing key rotation behavior.
- Existing provider integrations remain unaffected.

Run backend and frontend builds and relevant test suites.

Do not communicate with live providers or control physical devices during testing.

## 8. Scope and completion

This is a refinement of Phase 6A, not Phase 6B.

Do not implement provider cutover, automatic credential import or changes to existing OAuth workflows.

At completion:

1. Summarize the changes.
2. Explain the updated storage model.
3. List any database migrations and deployment steps.
4. Report build and test results.
5. Update `docs/architecture/09-configuration-vault.md`.
6. Update `docs/AI_HANDOFF.md`.
7. Archive this prompt under `docs/prompts`.
8. Commit locally without pushing.

Keep the application operational and all existing provider integrations unchanged.

## Execution summary

- Added [migration 015](../../database/migrations/015_configuration_identifiers.sql) for scope-restricted, readable identifiers. Migration 014 retains its original checksum. Settings now renders saved identifiers and edits them separately from write-only AES-256-GCM secret slots.
- Added explicit, transactional identifier conversion for profiles saved under 014. Missing keys or conflicting values stop conversion without committing any changes. The command preserves nonce history and genuine secret envelopes. Provider configuration and OAuth stores remain on their existing paths.
- Both builds passed. xUnit recorded 167 passes and nine unrelated opt-in skips, including the vault upgrade and Dashboard regression on disposable MySQL 8.0.46. The conversion CLI reported zero on rerun. Vitest recorded 51 passes.
- The [vault guide](../architecture/09-configuration-vault.md#convert-identifiers-staged-under-migration-014) documents the approved deployment steps. The development and live databases were untouched during verification. Updated the [handoff](../AI_HANDOFF.md).
