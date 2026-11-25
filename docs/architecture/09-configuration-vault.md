# Configuration and encrypted credentials

## Configuration inventory

ASP.NET Core uses its normal configuration precedence. The checked-in JSON files carry non-secret defaults. Development User Secrets, process environment variables, and command-line arguments can supply overrides. Compose supplies container environment settings and persists the ASP.NET Core Data Protection key ring. Existing provider options are singleton snapshots. Restart the API to change them.

| Settings | Current source and scope | Defaults and validation | Sensitivity and disposition |
| --- | --- | --- | --- |
| Database host, port, name, user, password, TLS mode | `Database` configuration, installation bootstrap | Connection details required. Port 1–65535. Use the scoped runtime account or the scoped migration account for an administrative operation. | Password is secret. Keep this section external. |
| ASP.NET hosting, origins, proxy, logging, HTTPS, Data Protection | Host configuration, launch settings, Vite environment, `DataProtection:KeyDirectory`, and code defaults, installation-wide | Retain loopback and trusted proxy settings. Persist the Data Protection key ring for cookie and existing OAuth compatibility. | Treat private TLS and key-ring material as secrets. Keep outside MySQL. |
| Bootstrap viewer and administrative property setup | `Bootstrap` and `PropertyAdmin`, one-shot CLI | Valid local email, password policy and explicitly selected property. Commands validate existing membership and preserve existing credentials. | Viewer password is secret. Keep these transient and external. |
| Simulator and worker defaults | `Simulator` configuration, installation-wide | Existing enabled demo, 10-second polling, 45-second freshness and Demonstration scenario in appsettings. Polling is 2–900 seconds and freshness at least twice the interval. | Non-secret. Keep simulator and hosting configuration outside this milestone. |
| Growatt | `Growatt` configuration, one installation selected by `SiteKey` | `Enabled`, site key, Master/Slave IDs, nominated shared battery source and verified time zone. Preserve 300-second poll, 900-second freshness and 15-second timeout defaults, monotonic site/aggregation semantics and opt-in diagnostics. | `ApiToken` is secret. Site/device identifiers and operating settings are non-secret, scope-restricted data. Settings stages the token and poll cadence. |
| Daikin ONECTA | `Daikin` configuration, one OAuth client and designated operator. Grant and inventory use existing owner and property bindings. | Preserve callback, operator identity, PKCE, write flag, 15-minute poll, 35-minute freshness, 200-call limit and 40-call background reserve. Validate callback origin and retain current write policy. | Client ID is non-secret and client secret is encrypted. Callback and policy settings are non-secret. `TokenKey` is external legacy decryption material. Existing access and refresh tokens remain encrypted in the existing Daikin tables. |
| eWeLink | `Ewelink` configuration, one developer app and designated operator. Grant, inventory, and devices retain ownership and property bindings. | Preserve HTTPS callback, five-minute poll, optional WebSocket, reporting, relay write and diagnostics flags. Existing device preferences and safety flags are database-backed. `RelayWriteDevices` is legacy bootstrap fallback keyed by the stable device ID. | App ID is non-secret and app secret is encrypted. Existing tokens and account key are encrypted with ASP.NET Core Data Protection. Callback, IDs and policy settings are access-controlled operational data. Keep the key ring outside MySQL. |
| Tuya / Smart Life | `Tuya` configuration, one regional Cloud project and linked UID; devices are property-assigned | Allowlisted data center, single designated operator, 30-minute poll, three history reads per poll by default. Validate project/user identifiers and use existing signed requests. | Access ID and linked user ID are non-secret. Access secret is encrypted. Region and operating settings are scope-restricted, non-secret data. The existing short-lived access token is held in process memory. |
| Device and property preferences | Existing MySQL inventory, property memberships, rooms, provider bindings, command safeguards and dashboard tables. Browser storage for view mode. | Retain stable IDs, authorization, telemetry freshness and confirmation protections. Settings in these stores already take effect through existing read paths. | Keep telemetry, identity, authorization, and equipment settings in their current owners. The provider vault does not duplicate them. |

Diagnostics and aggregation switches remain off by default unless the existing provider configuration specifies otherwise. Keep provider-specific ranges, callback paths and identifier validation in their existing adapters when adding cutover.
Operator emails and linked user identifiers can identify people. They are access-controlled account data. The vault exposes only the four staged identifiers listed below and reveals them only within the authorized scope.

## Phase 6A setup

Phase 6A provides a staging vault. Existing workers, OAuth flows, and real device permissions continue to use their current configuration. A staged credential's **Configured** status means a value was saved. It does not verify provider access.

1. Back up the dedicated database. On an approved target, use the scoped migration account and run `dotnet run --project src/backend/HomeAutomation.Api -- --migrate` using the [migration-account workflow](../../README.md#apply-migrations-and-provision-a-viewer). Migration [014](../../database/migrations/014_configuration_vault.sql) creates the profile, audit and nonce tables. Migration [015](../../database/migrations/015_configuration_identifiers.sql) adds the readable `identifiers` JSON column with an empty object default. The runner records checksums automatically and recognizes an existing 014. Settings require both ledger entries. Other application features can start while Settings is unavailable.
2. Provision an external directory containing a raw 32-byte key file named `<key-id>.key`. Use a different key and ID for each environment and rotation. Keep the directory outside the checkout, readable only by the application identity and the responsible administrator. Never reuse a key ID for different key material or register the same material under more than one ID.
3. Set `ConfigurationVault:KeyDirectory` to the absolute directory and `ConfigurationVault:ActiveKeyId` to the key ID. IDs use 1–64 lowercase ASCII letters, digits, hyphens or underscores. Set `ConfigurationVault:AdministratorUserId` only if global edit access is needed. Specify the exact existing Identity user GUID using an approved administrative lookup. An administrator must also have a property membership to reach Settings through the SPA. Changing these bootstrap settings requires an API restart.
4. Open **Settings** for an authorized property. Owner and Admin may stage changes. Operator and Viewer have read-only access to the same redacted profile. Global access requires the explicitly designated installation administrator. A property's profile is staged independently of the global scope.

Settings changes require HTTPS outside loopback Development. The HTTPS origin must be recognized as secure by ASP.NET Core. Use the [local HTTPS setup](../../README.md#local-https-for-onecta-on-windows) for Development and a correctly configured trusted TLS termination path for deployment.

The runtime account requires only its existing `SELECT`, `INSERT`, and `UPDATE` grants for these tables. Removal changes the encrypted profile document and does not require SQL `DELETE`. The rotation command also uses those routine DML permissions.

### Convert identifiers staged under migration 014

Migration 015 adds storage and preserves every existing staged value. Identifier conversion requires an explicit maintenance command. For an approved upgrade, stop old API processes, back up the schema and external key files, and apply migration 015 through the runner. With the new version, Settings flags legacy identifiers as pending conversion. Other fields can still be edited.

```powershell
dotnet run --project src/backend/HomeAutomation.Api -- --convert-configuration-identifiers
```

Run this command with the runtime account and the original external key directory. It uses each saved envelope's key ID. It authenticates the envelope against its original provider, scope and field, copies the recovered value into `identifiers`, removes the obsolete envelope and audits the field change in the same transaction. Conflicting values, missing keys and invalid envelopes abort the whole operation. Restore the relevant key or resolve the conflict with the database administrator and rerun. Successful reruns report zero pending conversions. A converted profile advances its revision and rejects stale edits. Historical nonce reservations remain reserved.

### Local Windows key example

Create the key in a new NTFS directory for the Windows account that will run the API. Use a private path with restrictive ACLs. This example updates your local Development bootstrap settings.

```powershell
$vaultDirectory = Join-Path $env:LOCALAPPDATA 'HomeAutomation-Vault-Dev'
$vaultId = 'dev-001'
if (Test-Path -LiteralPath $vaultDirectory) { throw 'Use an existing vault or choose a new protected directory.' }
New-Item -ItemType Directory -Path $vaultDirectory -ErrorAction Stop | Out-Null
$operatorSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
icacls.exe $vaultDirectory /inheritance:r /grant:r "*${operatorSid}:(OI)(CI)F"
if ($LASTEXITCODE -ne 0) { throw 'Could not restrict the vault directory.' }
$key = New-Object byte[] 32
$generator = [Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $generator.GetBytes($key)
    $file = [IO.File]::Open((Join-Path $vaultDirectory "$vaultId.key"), [IO.FileMode]::CreateNew)
    try { $file.Write($key, 0, $key.Length) } finally { $file.Dispose() }
} finally {
    [Array]::Clear($key, 0, $key.Length)
    $generator.Dispose()
}
dotnet user-secrets set 'ConfigurationVault:KeyDirectory' $vaultDirectory --project src/backend/HomeAutomation.Api
dotnet user-secrets set 'ConfigurationVault:ActiveKeyId' $vaultId --project src/backend/HomeAutomation.Api
```

For a future Synology Compose deployment, mount the protected host key directory read-only into the API container. Set `ConfigurationVault__KeyDirectory` to its container mount path and `ConfigurationVault__ActiveKeyId` to the production ID. Use permissions aligned with the container's runtime UID. Key bytes stay in the mounted files. Use a separate mount and key ring for ASP.NET Core Data Protection.

## Storage, backup and rotation

- `poll_seconds` is a normal integer field. `identifiers` contains Daikin `clientId`, eWeLink `appId`, or Tuya `accessId` and `userId` as readable JSON strings in the profile's global or property scope. Omitted identifiers keep their saved values. Clearing a text input clears that identifier. New encrypted slots are limited to Growatt `apiToken`, Daikin `clientSecret`, eWeLink `appSecret`, and Tuya `accessSecret`.
- AES-256-GCM uses a 12-byte CSPRNG nonce and a 16-byte tag. Format version, key ID, scope, provider, and slot are authenticated as associated data. The committed nonce ledger enforces uniqueness per key ID, including after replacement or removal. Buffers for loaded keys and temporary UTF-8 plaintext are cleared when practical. ASP.NET and browser inputs can temporarily retain immutable strings in process memory.
- Each update locks the scope/provider revision, validates the interval and allowed fields, then saves the profile and audit records in one transaction. Concurrent changes return a conflict. Responses show saved identifiers and only presence flags for secrets. Blank secret inputs preserve their values. The removal checkbox explicitly clears the staged secret. Audit rows contain only timestamp, actor, scope, provider, setting name and action.
- Settings reads the latest committed revision on the next request. A provider uses a single options snapshot for the life of the process. Vault edits take effect on restart only for providers explicitly configured to use the vault.
- Back up the database including the profiles, nonce history and audit rows. Separately back up all referenced external key files in encrypted storage with restricted access. Keep the referenced key versions for as long as any retained database snapshot requires them. Keep legacy Daikin and Data Protection material while legacy grants are needed. Test recovery in an isolated environment with networking disabled or mocked.
- For rotation, finish pending identifier conversion first. Stop API instances, back up database and key files, add a new key under a new ID, and temporarily select the new active ID while retaining the old key files. Run `dotnet run --project src/backend/HomeAutomation.Api -- --rotate-configuration-keys` against the approved database. It authenticates the genuine secret envelopes, re-encrypts those using older IDs, and commits them and their audit rows together. The same command with the same active key is an authenticated integrity check and changes no already-current credentials. A failure rolls back the transaction. Restart all instances with the same active key once rotation succeeds.
- On recovery, restore the matched database and key material before starting Settings or running a rotation. If a snapshot may omit later nonce reservations, generate a new active ID and key and rotate during recovery before new writes. Missing or incorrect keys fail closed. Restore originals from secure backup or deliberately replace the affected credential through an authorized operational process. The app never generates a substitute key.

## Phase 6B provider cutover

Apply [migration 016](../../database/migrations/016_provider_runtime_configuration.sql) through the existing runner on an approved database. It adds a nullable `operational_settings` JSON column. Migrations 014 and 015 remain intact. Settings staging and legacy startup continue to work without migration 016. Import and vault activation require all three migration ledger entries. No additional runtime grants are needed.

### Import and validate one provider

Run the command under the intended deployment configuration with the same User Secrets, environment and appsettings precedence that the legacy process uses. Use the runtime database account and the active vault key. For OAuth providers, keep the original grant key material available. The dry run validates the typed options, checks the selected scope and reports fields, result and current revision. It uses read-only database operations, with no provider requests and no vault writes.

```powershell
dotnet run --project src/backend/HomeAutomation.Api -- --import-provider=growatt --provider-property=1 --dry-run
dotnet run --project src/backend/HomeAutomation.Api -- --import-provider=growatt --provider-property=1
```

Use the actual property ID or omit `--provider-property` for a global Growatt profile. Growatt's SiteKey must match an existing property and, for scoped imports, the selected property. Daikin, eWeLink and Tuya currently use a single account each. Import them globally with `--import-provider=daikin`, `--import-provider=ewelink` or `--import-provider=tuya`. Property-level staging remains available. It cannot substitute for one of those installation-wide account profiles.

The import fills an empty or compatible profile, keeps identical encrypted values, and reports `unchanged` for a repeated identical import. If existing values differ, review the profile and rerun with `--replace-revision=<reported revision>` only when the replacement is intended. A stale revision fails. Encrypted identifiers must be converted separately before import. A new profile carries the existing cadence, identities, callbacks, feature flags, diagnostics, relay fallback map, quotas, freshness and aggregation settings as applicable to that provider. Genuine secrets use the existing vault envelope and nonce ledger. Legacy configuration is retained for rollback.

The import checks the owner of an existing local OAuth account. The write operation and vault startup also check that saved grants can be decrypted locally by their existing mechanisms. Daikin still requires the existing external `Daikin:TokenKey`. eWeLink still uses the original ASP.NET Core Data Protection key ring. Neither is part of the imported profile. The imported identity is recorded so a later identifier edit cannot silently repoint an active client or project. An existing OAuth grant must be disconnected through its normal operator workflow before changing that linked identity. Successful local validation establishes configuration integrity and grant decryption. Live provider acceptance is a separate operational check.

### Activate and roll back

1. Import and review the chosen provider while its source is still `Legacy`. Confirm its site or linked account, callback and write policy. Complete or cancel in-progress OAuth and device operations before stopping the API.
2. Set `ConfigurationVault:Providers:<provider>:Source` to `Vault` through the deployment administrator's configuration. For a property-scoped Growatt import, also set `ConfigurationVault:Providers:growatt:PropertyId` to that property ID. The environment equivalents use double underscores, for example `ConfigurationVault__Providers__growatt__Source`.
3. Restart a single API instance. A missing, unreadable, mismatched or invalid selected profile stops startup with a provider-specific diagnostic. Structured startup event 6100 shows source, scope and local validation status. Use the [read-only verification command](../../README.md#verify-provider-startup-on-windows) and confirm successful normal polling, assignments, measurements and write controls from the existing pages.
4. To roll back, stop the API, reset that provider's source to `Legacy` or remove its selector, clear any Growatt `PropertyId` override and restart with the original legacy configuration and key material. Keep the current database. Both modes use the same encrypted OAuth grant rows, so restoring a pre-cutover database snapshot could lose a refreshed grant.

## Verification

Run the normal backend and frontend suites. The opt-in MySQL integration test expects `HOMEAUTOMATION_CONFIGURATION_TEST_MYSQL` and `HOMEAUTOMATION_CONFIGURATION_TEST_RUNTIME`. Use a disposable loopback MySQL instance on a non-default port, schema `homeautomation_configuration_test`, and runtime account `configuration_runtime` with only SELECT, INSERT and UPDATE. The fixture applies migrations as its local administrative account and uses synthetic credentials. Automated tests require no live provider access.

The provider cutover fixture uses `HOMEAUTOMATION_PROVIDER_TEST_MYSQL` and `HOMEAUTOMATION_PROVIDER_TEST_RUNTIME` against a separate disposable schema named `homeautomation_provider_test` and runtime account `provider_runtime` with the same scoped permissions.
