# HomeAutomation — Milestone 6: Database Configuration and Encrypted Secrets

Date: 2026-11-25

## Original prompt

# HomeAutomation — Milestone 6: Database Configuration and Encrypted Secrets

Implement centralized, database-backed configuration management for HomeAutomation, including secure storage of credentials and secrets using AES-256-GCM.

The goal is to eliminate provider-specific configuration stored in environment variables or application configuration files, except for the minimal bootstrap settings required to start the application.

This is a security-sensitive architectural change. Prioritize correctness, backward compatibility, controlled migration and preservation of existing working integrations.

Use High reasoning.

## 0. Read project context

Before starting:

- Read AGENTS.md and docs/AI_HANDOFF.md.
- Review relevant ADRs, architecture documentation and recent commits.
- Inspect the current Git status.
- Use Graphify for targeted code navigation if available.
- Identify all configuration sources currently used by the application.
- Identify the configuration and credential requirements of Growatt, Daikin ONECTA, eWeLink and Tuya.

Do not assume that all providers use the same authentication mechanism.

Create a short implementation plan before changing code.

## 1. Configuration inventory

Identify and classify existing application settings:

- Global application settings.
- Property-specific settings.
- Provider integration configuration.
- API credentials, access tokens and refresh tokens.
- Polling intervals and operational thresholds.
- Device configuration.
- Settings that must remain available during application bootstrap.

For each setting, determine:

- Current source.
- Whether it is sensitive.
- Whether it is global or property-scoped.
- Whether changing it requires restarting a service.
- Appropriate default and validation rules.

Avoid moving unrelated ASP.NET Core hosting or infrastructure configuration into the database without a clear benefit.

## 2. Database-backed configuration

Implement a centralized configuration service using the existing .NET 10, Dapper and MySQL architecture.

Requirements:

- Support global and property-scoped settings.
- Support typed configuration and validation.
- Persist configuration changes transactionally.
- Enforce property-scoped access permissions.
- Avoid duplicated settings and conflicting sources of truth.
- Support configuration caching where appropriate.
- Define how configuration changes become visible to running workers.
- Prevent partial configurations from breaking provider initialization.

Prefer the existing repository and service patterns.

Use a small, maintainable schema rather than introducing an unnecessary configuration framework.

## 3. AES-256-GCM secret encryption

Implement secret encryption using:

`System.Security.Cryptography.AesGcm`

Requirements:

- AES-256-GCM with 256-bit keys.
- Generate a fresh, cryptographically secure 96-bit nonce for every encryption operation.
- Use a 128-bit authentication tag.
- Store ciphertext, nonce, authentication tag, key identifier and encryption format version.
- Never reuse a nonce with the same key.
- Authenticate contextual metadata such as configuration identity and property scope using associated authenticated data (AAD).
- Detect tampering and reject invalid ciphertext.
- Fail closed when a key is missing or decryption fails.
- Do not silently regenerate a key if encrypted secrets already exist.
- Support future encryption key rotation.
- Clear sensitive temporary buffers where practical.

Use standard .NET cryptographic APIs.

Do not create a custom encryption algorithm.

## 4. Encryption key management

The encryption master key must never be stored in MySQL.

Keep the minimum bootstrap configuration outside the database:

- Database connection information.
- Encryption key or secure key location.
- Essential application startup settings.

For local Windows development, support a securely stored external master key.

For future Synology Docker deployment, support an externally mounted secret or equivalent protected secret mechanism.

Requirements:

- Never commit encryption keys.
- Never place keys in tracked appsettings files.
- Never log encryption keys.
- Do not expose keys through APIs.
- Document secure backup and restoration of encryption keys.
- Support separate encryption keys for development and production.
- Define a safe key rotation procedure with key identifiers.

Do not introduce external paid key-management dependencies.

## 5. Provider configuration migration

Migrate the existing providers to the new configuration service:

- Growatt.
- Daikin ONECTA.
- eWeLink.
- Tuya / Smart Life.

Preserve:

- Existing OAuth flows.
- Access-token and refresh-token management.
- Provider polling intervals and throttling.
- WebSocket functionality.
- Device discovery.
- Telemetry persistence.
- Device-control permissions and safeguards.

Ensure tokens that are refreshed automatically are securely persisted using the new encrypted storage mechanism.

Do not create competing stores of credentials.

## 6. Safe migration from existing settings

Implement a controlled migration path from existing environment-based configuration.

Requirements:

- Detect existing configuration without exposing secret values.
- Provide an explicit import procedure or administration command.
- Encrypt credentials before persisting them.
- Validate imported configuration.
- Prevent accidental overwriting of valid existing credentials.
- Make imports idempotent where practical.
- Never automatically delete existing configuration or secrets.
- Do not activate database-based credentials until they have been validated.
- Preserve a documented rollback procedure.

Do not automatically modify the live database or execute an import against real provider credentials.

After successful validation, environment-based provider configuration can be deprecated and eventually removed.

Bootstrap configuration must remain external.

## 7. Settings management UI

Extend the existing Settings area to support managing configuration.

Requirements:

- Display configured providers and their current configuration status.
- Allow authorized users to update relevant global or property-level settings.
- Allow secure replacement of provider credentials.
- Never return existing plaintext secrets to the frontend.
- Display secrets as Configured / Not configured, not their actual values.
- Leaving a secret field blank must not accidentally erase an existing value.
- Provide an explicit action for removing a stored credential.
- Validate configuration before saving.
- Prevent unauthorized cross-property access.
- Preserve the existing visual design.

Do not expose encryption keys or infrastructure bootstrap settings in the UI.

## 8. Audit logging

Record important configuration changes.

Include:

- Timestamp.
- Acting user.
- Property.
- Configuration identifier.
- Action type: created, updated, removed or credential rotated.

Never store plaintext credentials, encryption keys or secret values in audit records.

Avoid logging full configuration objects when they might contain sensitive values.

## 9. Database migrations

Use the project's existing migration framework.

Important:

- Create versioned SQL migration files.
- Record applied migration names and checksums using the existing migration mechanism.
- Do not apply SQL files manually without registering them in schema_migrations.
- Do not execute migrations against the live database.
- Do not broaden runtime database permissions unnecessarily.
- Document the exact migration and deployment procedure.

Validate compatibility with the existing MySQL 8.0 development environment.

## 10. Security and regression tests

Add tests covering:

- Encryption and decryption.
- Unique nonces.
- Authentication-tag validation.
- Ciphertext modification detection.
- Missing or incorrect encryption keys.
- Key rotation.
- Invalid or missing configuration.
- Secret redaction.
- Property authorization.
- Configuration caching and refresh.
- Concurrent configuration updates.
- Provider credential migration.
- OAuth token refresh persistence.
- Database migration compatibility.
- Backup and restore of encrypted credentials with their keys.

Run relevant backend/frontend tests and builds.

Use mocked providers for automated tests.

Never send control commands to physical devices during testing.

## 11. Implementation phases

Implement in two controlled phases.

### Phase 6A — Configuration infrastructure

Complete:

- Configuration inventory.
- Database schema.
- Configuration service.
- AES-256-GCM encryption.
- External key management.
- Secure settings APIs and management UI.
- Audit logging.
- Tests and documentation.

The existing provider integrations must continue working unchanged during this phase.

Commit Phase 6A separately.

### Phase 6B — Provider migration

After Phase 6A is stable:

- Integrate Growatt, Daikin, eWeLink and Tuya.
- Implement controlled credential import.
- Preserve existing provider behavior.
- Add migration and regression tests.
- Document manual activation and rollback.
- Avoid switching live credentials without explicit authorization.

Commit Phase 6B separately.

If implementation cannot be completed safely in one session, stop at a stable phase boundary. Record what remains in docs/AI_HANDOFF.md so Codex or Claude Code can continue without repeating the work.

## 12. Completion

At completion:

1. Report implemented functionality and architectural decisions.
2. List created database migrations.
3. Document bootstrap configuration requirements.
4. Document the encryption key backup and recovery procedure.
5. Describe how existing credentials will be migrated safely.
6. Report tests and build results.
7. Update docs/AI_HANDOFF.md.
8. Archive this prompt under docs/prompts.
9. Commit locally without pushing.

Do not change live database contents, live provider credentials or physical devices without explicit authorization.

Preserve all existing functionality and avoid unrelated refactoring.

## Execution summary

Phase 6A adds [migration 014](../../database/migrations/014_configuration_vault.sql), a scoped configuration staging service and Settings UI, AES-256-GCM credential storage with external key files, revision-based concurrency, and transactional audit records. Existing providers still read their legacy sources. The [vault guide](../architecture/09-configuration-vault.md) records the configuration inventory, key provisioning, backup, restoration and rotation.

Both builds passed. Back end tests recorded 167 passes and nine unrelated opt-in skips. The configuration and dashboard integration regressions ran on disposable MySQL 8.0.46. All 49 frontend tests passed.

Work stopped at the verified Phase 6A boundary. Phase 6B still needs complete typed provider import and activation, plus continuity and rollback tests for rotated OAuth grants. The [handoff](../AI_HANDOFF.md) records the relevant provider entry points and decisions. No live database, credentials, or devices were changed.
