# Configuration Vault global profile visibility in Settings

Date: 2026-11-26

## Original prompt

Investigate and fix Configuration Vault profile visibility in HomeAutomation Settings.

Database findings:
- daikin: scope_id=0, property_id=NULL, revision=1
- ewelink: scope_id=0, property_id=NULL, revision=1
- tuya: scope_id=0, property_id=NULL, revision=1
- growatt: scope_id=1, property_id=1, revision=1

All four profiles contain stored credentials.

The React Settings page currently reports missing credentials for eWeLink and Tuya.

Investigate whether the Settings API incorrectly filters out global profiles when loading configuration for property 1.

Requirements:
1. Review the intended semantics of scope_id and property_id.
2. Trace the profile resolution logic from database through API to React.
3. Check whether global profiles should be inherited by property-scoped configuration.
4. Identify the actual root cause before implementing changes.
5. Preserve all existing encrypted credentials and configuration records.
6. Ensure the UI correctly displays credential presence without revealing secrets.
7. Add regression tests for global and property-scoped profile resolution.

Do not directly modify database records or change scope semantics without verifying the architecture.

Implement the minimal safe fix and report the root cause.

## Execution summary

**Semantics (verified in the configuration vault guide)**
- `scope_id = 0` (with `property_id` NULL) is the global scope. `scope_id = N` is property N.
- A property profile is staged independently. Daikin, eWeLink and Tuya are single installation-wide accounts imported globally; a property profile cannot substitute for them. Growatt may use a property profile.
- Profiles are not inherited, and runtime selection of a profile comes from `ConfigurationVault:Providers:<provider>`.

**Root cause**
- **The API filter is correct by design.** `ProviderConfigurationStore.ReadAsync` loads only `scope_id = <property>` for a property view.
- **The UI rendering is the defect.** The Settings page renders the property section with all four providers. Daikin, eWeLink and Tuya have no property rows, so their cards showed "Not configured" and "Staged configuration incomplete". The global section, where their credentials are, is returned only to the designated installation administrator; for anyone else the API returns 404 and the page hides it.
- So the page reported the installation-wide accounts as missing even though the global profiles hold credentials.

**Fix**
- **Backend:** each property view now carries `installationWide` and a presence-only `global` summary (revision, plus credential and identifier booleans) when a global profile exists. No values, envelopes or ciphertext are exposed.
- **Unchanged:** scope semantics, save behavior (a property page still writes the property profile), the administrator-only global section, and every stored record.
- **Frontend:** on a property page, installation-wide cards show the global profile status ("Installation-wide profile complete", "incomplete" or "No installation-wide profile"), list its presence flags, explain that property fields stage a separate profile, and label empty property slots "Not staged for this property".

**Verification**
- **New MySQL regression test** (`ConfigurationScopeMySqlTests`), on the disposable MySQL 8.0.46 schema with the DML-only runtime account:
  - the reported layout resolves with global presence and without inheritance;
  - no values or envelope fields appear in the property view;
  - the global view is still administrator-only;
  - a property save leaves the global row untouched.
- **Backend:** Release build passed with zero warnings. Tests reported 321 passes and 11 opt-in skips with fixtures; without fixtures, 293 passes and 39 skips.
- **Frontend:** production build passed and Vitest reported 95 passes, including 2 new Settings tests.
- **Not touched:** the live database and the backend.

**References**
- `src/backend/HomeAutomation.Infrastructure/Configuration/ProviderConfigurationStore.cs`
- `src/backend/HomeAutomation.Application/ProviderConfiguration.cs`
- `src/frontend/home-automation-web/src/features/ConfigurationSettings.tsx`
- [Configuration vault guide](../architecture/09-configuration-vault.md)
