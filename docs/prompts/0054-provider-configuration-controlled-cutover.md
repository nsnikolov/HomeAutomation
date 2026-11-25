# HomeAutomation — Milestone 6B: Provider Configuration Migration and Controlled Cutover

Date: 2026-11-25

## Original prompt

# HomeAutomation — Milestone 6B: Provider Configuration Migration and Controlled Cutover

Implement Phase 6B of the Configuration Vault project.



Milestone 6A was completed in commit `e470cf6`.\
Milestone 6A.1 was completed in commit `abdccd6`.



Database migrations 014 and 015 have already been applied to the development database.



The Configuration Vault now supports:

- Global and property-scoped configuration.
- AES-256-GCM encryption for actual secrets.
- Plaintext storage for non-secret identifiers and operational settings.
- Secure Settings UI.
- Audit logging.
- External encryption keys and key rotation.
- Transactional configuration updates.

The goal of this milestone is to migrate the existing provider integrations to the database-backed configuration system without interrupting working integrations or changing physical device behavior.



Use High reasoning.

## 1. Read project context

Before implementation:

- Read AGENTS.md and docs/AI_HANDOFF.md.
- Read docs/architecture/09-configuration-vault.md.
- Inspect recent Git commits and working tree status.
- Review the configuration and authentication implementations for Growatt, Daikin ONECTA, eWeLink and Tuya.
- Identify all provider initialization, polling, OAuth and credential persistence paths.

Use existing project conventions.



Do not introduce an unrelated architectural rewrite.

## 2. Provider configuration integration

Implement database-backed configuration for:

1. Growatt
2. Daikin ONECTA
3. eWeLink
4. Tuya / Smart Life

Each provider must be able to obtain its configuration from the Configuration Vault.



Maintain existing global and property scoping rules.



Preserve:

- Provider authentication.
- Device discovery.
- Polling schedules.
- Telemetry collection.
- SignalR updates.
- Device ownership and property assignments.
- Device control permissions.
- Existing provider safety restrictions.
- Rate limits and API quotas.
- Existing OAuth redirect and callback behavior.

Use the existing configuration model and repository patterns.

## 3. Configuration classification

Keep the classification implemented in Milestone 6A.1.



Encrypted secrets:

- Growatt API Token.
- Daikin Client Secret.
- eWeLink App Secret.
- Tuya Access Secret.
- Any newly persisted authentication secrets or tokens.

Ordinary values:

- Daikin Client ID.
- eWeLink App ID.
- Tuya Access ID.
- Tuya Linked User ID.
- Polling intervals.
- Other explicitly non-secret provider settings.

Do not move plaintext secrets into ordinary settings.



Do not encrypt non-secret identifiers merely because they belong to provider credentials.



Preserve the existing secure storage mechanisms for OAuth grants unless an explicitly designed, tested migration makes a change necessary.

## 4. Explicit provider configuration source

Introduce a controlled configuration source selection mechanism.



Each provider must support:

- Legacy configuration source.
- Database Configuration Vault source.

Requirements:

- Legacy remains the default until explicitly activated.
- Never mix individual configuration values from different sources during one provider initialization.
- A selected database configuration must be complete and valid.
- If database configuration is missing or invalid, fail clearly rather than silently falling back to legacy credentials.
- Activation must require an explicit administrator action.
- Use restart-based activation initially, preserving the existing singleton configuration model.
- Never switch credential sources while a provider operation is running.

Avoid maintaining competing active configurations.



Design source selection to support gradual provider-by-provider activation.

## 5. Controlled credential import

Implement an explicit import command or supported administrative operation.



Requirements:

- Read existing provider configuration using established configuration precedence.
- Classify secret and non-secret settings correctly.
- Encrypt secrets before storing them.
- Preserve all relevant provider identifiers and operational settings.
- Validate imported settings using existing provider validation rules.
- Never log plaintext credentials.
- Never return plaintext secrets in diagnostics.
- Do not overwrite already configured database values without explicit permission.
- Make import idempotent where practical.
- Support importing one provider at a time.
- Provide dry-run validation that reports field names and configuration status without secret values.
- Clearly report missing required fields.

Do not automatically execute imports against the live development database.



Do not remove existing credentials during import.

## 6. Provider-specific considerations

### Growatt

Preserve the existing:

- SiteKey.
- Master and Slave device identifiers.
- Shared battery aggregation semantics.
- Polling and freshness configuration.

## Execution summary

- Added [migration 016](../../database/migrations/016_provider_runtime_configuration.sql), explicit per-provider startup selection, and redacted, revision-guarded import with dry-run validation. Growatt can load an explicitly matched property profile. Daikin, eWeLink and Tuya use installation-wide profiles. Legacy remains the default.
- Existing adapters receive their original typed options from the selected source. The OAuth grant stores and external grant keys remain authoritative across both sources. See [import and rollback instructions](../architecture/09-configuration-vault.md#phase-6b-provider-cutover).
- Both builds passed. Backend tests recorded 174 passes and nine unrelated opt-in skips, including the cutover, vault, and Dashboard regressions on disposable MySQL 8.0.46. All 51 frontend tests passed. The CLI smoke test verified a no-write dry run and a failed startup when the selected profile was missing. The live database and providers were not contacted. Updated the [handoff](../AI_HANDOFF.md).
