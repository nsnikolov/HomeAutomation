# Daikin ONECTA Cloud

## API and verification

Daikin offers self-service private EMEA integrations through its Developer Portal. Create an application, accept the applicable terms, and register the exact redirect URI. Daikin publishes a private-app quota of 200 calls per day with sliding-window accounting. Its authenticated documentation catalog returned HTTP 401 without a developer session. No local ONECTA developer credentials were present. The official gateway endpoint also returned HTTP 401 without a token. [Daikin developer FAQ](https://www.daikin.eu/en_us/product-group/control-systems/daikin-developer-portal.html) · [ONECTA Developer Portal](https://developer.cloud.daikineurope.com/)

The adapter targets the ONECTA developer API, using the identity provider's `/v1/oidc/authorize` and `/v1/oidc/token` endpoints, scopes `openid onecta:basic.integration offline_access`, and a bearer-authenticated `GET /v1/gateway-devices`. Manual commands target an individual management point characteristic using PATCH. The public portal confirms the service and quota. The detailed wire mappings, credential exchange, PKCE, scopes and control paths were exercised with fixtures and still require validation against the authenticated app contract and hardware. Public identity metadata was unavailable during this check. [Public gateway endpoint](https://api.onecta.daikineurope.com/v1/gateway-devices) · [OAuth security best practice](https://www.rfc-editor.org/rfc/rfc9700.html)

## Connect the local account

1. Sign in to the [Daikin Developer Portal](https://developer.cloud.daikineurope.com/) and register a private application. Use the ONECTA account that can see the intended units. Review the app's API access, supported PKCE methods, client authentication, scopes, and redirect requirements.
2. Register the browser-facing callback exactly. For the Windows Vite workflow it is `http://localhost:5173/api/v1/daikin/callback` if the portal accepts loopback HTTP. If the portal requires HTTPS, use the same HTTPS application origin for sign-in and the callback, with that exact URI registered. The existing Vite proxy forwards the API callback to ASP.NET Core. Keep `localhost` and `127.0.0.1` distinct.
3. Set `Daikin:ClientId`, `Daikin:ClientSecret`, `Daikin:OperatorEmail`, `Daikin:RedirectUri`, and `Daikin:TokenKey` in the backend's .NET User Secrets. Use the existing trusted local user's sign-in email. That account still requires a property membership. Generate the token key **once** per database and keep it out of the database and backups of the database. This PowerShell example reads the secret without echoing it and avoids placing either key value in command arguments.

   ```powershell
   $daikinKey = New-Object byte[] 32
   $daikinRng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
   try { $daikinRng.GetBytes($daikinKey) } finally { $daikinRng.Dispose() }
   $daikinSettings = @{
     'Daikin:ClientId' = Read-Host 'Daikin client ID'
     'Daikin:ClientSecret' = [System.Net.NetworkCredential]::new('', (Read-Host 'Daikin client secret' -AsSecureString)).Password
     'Daikin:OperatorEmail' = Read-Host 'Home Automation operator email'
     'Daikin:RedirectUri' = 'http://localhost:5173/api/v1/daikin/callback'
     'Daikin:TokenKey' = [Convert]::ToBase64String($daikinKey)
     'Daikin:EnableWrites' = $false
   }
   $daikinSettings | ConvertTo-Json -Compress | dotnet user-secrets set --project src/backend/HomeAutomation.Api
   Remove-Variable daikinSettings,daikinKey,daikinRng
   ```

   Retain the existing key when reconfiguring an installation. For deployment, supply secrets through a protected backend secret store. S256 PKCE is on by default. Use `Daikin:UsePkce=false` only if the authenticated Daikin contract confirms that the confidential client cannot use PKCE.
4. Apply migration 003 with the dedicated migration account using the [database setup instructions](../../README.md). Restart one Home Automation backend process. Sign in to the same application origin you registered, select a property, select **Daikin ONECTA**, and choose **Connect account**. After granting consent on Daikin, choose **Finish connecting**. The callback uses a short-lived browser-bound state. The final POST needs the original local operator session, property membership and CSRF validation.
5. Check discovery and assign each unit from **Unassigned devices** to the active property and optionally a room. Switch property before assigning a unit elsewhere. Assignments use stable gateway and management point identities. The same unit can only be assigned to one property; in-property room changes are supported. Compare the available readings with ONECTA first and leave write execution disabled.

**Disconnect account** stops polling and clears the stored access and refresh tokens locally. Historical telemetry and assignments are retained. The authenticated contract for upstream revocation could not be verified, so use Daikin's account or app management to revoke its grant where available. Reconnect may be required after expiration or revocation.

The local MySQL 8.0.46 runtime account was checked and lacks `CREATE`. Migration 003 is pending. It uses additive tables in the configured schema and leaves the existing simulator and Growatt migrations in place. The runtime account should retain its existing scoped DML grants.

## Monitoring, quotas and manual requests

- Gateway discovery batches all management points. The adapter selects climate points and maps model/name, reachability, power, operating mode, room and outdoor temperature, current mode's target, humidity and available fan settings. Missing values remain unavailable. Periodized energy datasets need authenticated units, interval semantics and hardware verification before use. A unit's minimum heating setpoint comes from its active mode and advertised range; 10 °C support is unknown.
- Provider observation time, when supplied, is stored with telemetry and the database records receipt separately. On snapshots without a provider timestamp, receipt time is displayed explicitly. Those snapshots cannot authorize a physical write. Unknown or stale reachability never implies grid loss.
- The worker defaults to 15-minute reads and 35-minute freshness. A rolling MySQL budget counts discovery, refresh and command calls, with 40 of 200 slots reserved from background use. Each refresh retries a safe read at most once after an authorization failure. Throttling honors Retry-After when provided. Share a single app worker and avoid other integrations consuming the same quota. The Daikin worker is independent of Growatt and the simulator.
- Writes default to disabled. The operator must explicitly authorize a supervised hardware test before changing `Daikin:EnableWrites`. The command path checks property membership, fresh device timestamp, online state, settable capabilities, enumerated values and advertised numeric min/max/step. It audits a pending request before dispatch, serializes operations, skips an already-reported setting, and records rejection or uncertainty. Success requires subsequent observation of the requested setting. An unresolved request blocks later commands for that device. If in doubt, inspect the unit locally; the worker never retries a PATCH.
- OAuth tokens are encrypted using AES-GCM with owner-bound authenticated data and a separately configured key. No ONECTA password is collected. Keep the callback query out of proxy access logs. The app preserves its normal authentication and cookies.
- A grid outage policy, changes from 16 °C to a low setpoint, and arrival behavior remain future work. Use an independent grid or ATS signal and local frost protection. Loss of Internet or Daikin availability leaves the unit under its own control. Later automation must honor manual overrides and must not repeat a command whose outcome is unknown.
