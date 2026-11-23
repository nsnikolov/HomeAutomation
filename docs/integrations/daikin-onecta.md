# Daikin ONECTA Cloud

## API and verification

Daikin offers self-service private EMEA integrations through its Developer Portal. Create an application, accept the applicable terms, and register the exact redirect URI. Daikin publishes a private-app quota of 200 calls per day with sliding-window accounting. Its documentation catalog returned HTTP 401 without a developer session. At initial implementation, the official gateway endpoint also returned HTTP 401 without a token. [Daikin developer FAQ](https://www.daikin.eu/en_us/product-group/control-systems/daikin-developer-portal.html) · [ONECTA Developer Portal](https://developer.cloud.daikineurope.com/)

The adapter targets the ONECTA developer API, using the identity provider's `/v1/oidc/authorize` and `/v1/oidc/token` endpoints, scopes `openid onecta:basic.integration offline_access`, and a bearer-authenticated `GET /v1/gateway-devices`. Manual commands target an individual management point characteristic using PATCH. The public portal confirms the service and quota. The detailed wire mappings, credential exchange, PKCE, scopes and control paths were exercised with fixtures and still require validation against the authenticated app contract and hardware. Public identity metadata was unavailable during this check. [Public gateway endpoint](https://api.onecta.daikineurope.com/v1/gateway-devices) · [OAuth security best practice](https://www.rfc-editor.org/rfc/rfc9700.html)

## Connect the local account

1. Sign in to the [Daikin Developer Portal](https://developer.cloud.daikineurope.com/) and register a private application. Use the ONECTA account that can see the intended units. Review the app's API access, supported PKCE methods, client authentication, scopes, and redirect requirements.
2. Register the browser-facing callback exactly. Use a named HTTPS origin that the browser can reach, such as `https://ha-dev.example.net:5443/api/v1/daikin/callback` with a hostname you control. Use that same origin for sign-in, starting OAuth, and finishing the connection. See [Diagnosing CloudFront 403](#diagnosing-cloudfront-403) for the loopback results and a local HTTPS setup.
3. Set `Daikin:ClientId`, `Daikin:ClientSecret`, `Daikin:OperatorEmail`, `Daikin:RedirectUri`, and `Daikin:TokenKey` in the backend's .NET User Secrets. Use the existing trusted local user's sign-in email. That account still requires a property membership. Generate the token key **once** per database and keep it out of the database and backups of the database. This PowerShell example reads the secret without echoing it and avoids placing either key value in command arguments.

   ```powershell
   $daikinKey = New-Object byte[] 32
   $daikinRng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
   try { $daikinRng.GetBytes($daikinKey) } finally { $daikinRng.Dispose() }
   $daikinSettings = @{
     'Daikin:ClientId' = Read-Host 'Daikin client ID'
     'Daikin:ClientSecret' = [System.Net.NetworkCredential]::new('', (Read-Host 'Daikin client secret' -AsSecureString)).Password
     'Daikin:OperatorEmail' = Read-Host 'Home Automation operator email'
     'Daikin:RedirectUri' = 'https://ha-dev.example.net:5443/api/v1/daikin/callback'
     'Daikin:TokenKey' = [Convert]::ToBase64String($daikinKey)
   }
   $daikinSettings | ConvertTo-Json -Compress | dotnet user-secrets set --project src/backend/HomeAutomation.Api
   Remove-Variable daikinSettings,daikinKey,daikinRng
   ```

   Retain the existing key when reconfiguring an installation. For deployment, supply secrets through a protected backend secret store. S256 PKCE is on by default. Use `Daikin:UsePkce=false` only if the authenticated Daikin contract confirms that the confidential client cannot use PKCE.
4. Apply migration 003 with the dedicated migration account using the [database setup instructions](../../README.md). Restart one Home Automation backend process. Sign in to the same application origin you registered, select a property, select **Daikin ONECTA**, and choose **Connect account**. After granting consent on Daikin, choose **Finish connecting**. The callback uses a short-lived browser-bound state. The final POST needs the original local operator session, property membership and CSRF validation.
5. Check discovery and assign each unit from **Unassigned devices** to the active property and optionally a room. Switch property before assigning a unit elsewhere. Assignments use stable gateway and management point identities. The same unit can only be assigned to one property. In-property room changes are supported. Compare the available readings with ONECTA before dispatching commands.

Manual AC writes default to enabled when the backend runs in Development and disabled in Production. Existing User Secrets and environment overrides still take precedence over the defaults. To explicitly disable local writes, set `Daikin:EnableWrites` to `false` in User Secrets and restart the backend.

```powershell
dotnet user-secrets set 'Daikin:EnableWrites' 'false' --project src/backend/HomeAutomation.Api
```

If `Daikin__EnableWrites` is set in the launcher or environment, set it to `false` there or remove it to allow the User Secrets value to take effect. Altherma monitoring remains read-only.

**Disconnect account** stops polling and clears the stored access and refresh tokens locally. Historical telemetry and assignments are retained. The authenticated contract for upstream revocation could not be verified, so use Daikin's account or app management to revoke its grant where available. Reconnect may be required after expiration or revocation.

Migration 003 uses additive tables in the configured schema and leaves the existing simulator and Growatt migrations in place. Apply it with the dedicated migration account and keep the runtime account's scoped DML grants.

## Diagnosing CloudFront 403

On 2026-11-21, the bare ONECTA authorize path returned HTTP 302. A controlled comparison held a synthetic, deliberately invalid client ID, scope, response type, state and S256 challenge constant. Only the callback URI changed. The checks stopped at the initial response.

| Callback variant | Initial response |
| --- | --- |
| HTTP on `localhost` | HTTP 403 CloudFront error page |
| HTTPS on `localhost` | HTTP 403 CloudFront error page |
| HTTPS on `127.0.0.1` | HTTP 403 CloudFront error page |
| HTTP on a named host | HTTP 302 to Daikin's login proxy |
| HTTPS on a named host | HTTP 302 to Daikin's login proxy |

The callback hostname is the strongest explanation for this early 403. HTTPS alone on a loopback hostname did not resolve it. The named HTTP response advanced to the login proxy, so it only establishes initial routing. It does not establish whether Daikin accepts HTTP later. The maintained `daikin-controller-cloud` client reports loopback rejection and recommends HTTPS. AWS documents that CloudFront alone cannot distinguish origin 403s from WAF blocks. [Community client documentation](https://github.com/Apollon77/daikin-controller-cloud#pre-requisites) · [CloudFront 403 documentation](https://docs.aws.amazon.com/AmazonCloudFront/latest/DeveloperGuide/http-403-permission-denied.html)

The authorization builder uses `client_id`, the configured `redirect_uri`, `response_type=code`, `scope=openid onecta:basic.integration offline_access`, and browser-bound `state`. With PKCE enabled it sends `code_challenge` and `code_challenge_method=S256`. The authorization-code exchange sends the exact configured redirect and the stored verifier. The official Developer Portal requires sign-in for the detailed contract. Public identity discovery did not return usable metadata during the check. Matching maintained client code corroborates the endpoint and scopes. The named HTTPS probe with S256 got past the initial CloudFront rejection. Full PKCE support and the redirect policy for the registered app still need an authenticated check. [Daikin developer documentation](https://developer.cloud.daikineurope.com/docs/b0dffcaa-7b51-428a-bdff-a7c8a64195c0/getting_started) · [Maintained ONECTA client endpoints](https://github.com/jwillemsen/daikin_onecta/blob/master/custom_components/daikin_onecta/const.py) · [Maintained client scopes](https://github.com/jwillemsen/daikin_onecta/blob/master/custom_components/daikin_onecta/config_flow.py)

### One HTTPS origin on Windows

For the Vite development workflow on the confirmed `ha-dev.britishlab.org` hostname, use [Local HTTPS for ONECTA on Windows](../../README.md#local-https-for-onecta-on-windows). Register `https://ha-dev.britishlab.org:5443/api/v1/daikin/callback` on the existing Daikin app, then start and finish the consent flow from that origin. The Kestrel-only setup below also serves a single HTTPS origin.

Use an existing trusted HTTPS deployment if available. For local-only development, use a hostname you control that resolves to `127.0.0.1` on the browser machine, for example through a Windows hosts entry or local DNS. Use a browser-trusted certificate with that hostname in its subject alternative names. A browser on a different machine needs a reachable address instead of its own loopback. The OAuth callback runs in the browser, so the local HTTPS listener can remain private.

1. Edit the existing Daikin app's registered redirect to `https://ha-dev.example.net:5443/api/v1/daikin/callback` with your hostname and port. Use the exact same URI in `Daikin:RedirectUri`. Keep `Daikin:UsePkce=true`. Leave the client credentials and writes flag as configured.
2. Serve the React build and API from that one HTTPS origin. The following example assumes a password-protected PFX for your hostname. Use the normal private certificate configuration for a shared environment. Use one running backend to avoid duplicate polling.

   ```powershell
   npm.cmd --prefix src/frontend/home-automation-web run build
   $daikinWebRoot = (Resolve-Path 'src/frontend/home-automation-web/dist').Path
   $env:ASPNETCORE_ENVIRONMENT = 'Development'
   $env:AllowedHosts = 'ha-dev.example.net'
   $env:Daikin__RedirectUri = 'https://ha-dev.example.net:5443/api/v1/daikin/callback'
   $env:Kestrel__Certificates__Default__Path = 'C:\path\to\ha-dev.pfx'
   $env:Kestrel__Certificates__Default__Password = [System.Net.NetworkCredential]::new('', (Read-Host 'Certificate password' -AsSecureString)).Password
   try {
     dotnet run --project src/backend/HomeAutomation.Api --no-launch-profile -- --urls https://127.0.0.1:5443 --webroot $daikinWebRoot
   } finally {
     Remove-Item Env:Kestrel__Certificates__Default__Password -ErrorAction SilentlyContinue
   }
   ```

3. Open `https://ha-dev.example.net:5443/` in the browser, sign in there, and use **Daikin ONECTA → Connect account**. Complete consent and **Finish connecting** in the same browser. That keeps the correlation cookie and the original operator session on the same host. [Kestrel HTTPS configuration](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0)

This controlled comparison with the existing registered app is the next diagnostic step. If the named HTTPS request still returns 403 before login, send Daikin support the UTC time, HTTP status, `x-amz-cf-id` or page Request ID, endpoint path, and callback URI. Ask whether they see a redirect-policy, application registration, or edge rejection and whether the app supports S256. Keep the full authorization URL, state, challenge, verifier, authorization code, cookies, and credentials out of diagnostics.

## Diagnosing the return from Daikin

Open the named HTTPS application origin, sign in with the operator account, and start one connection attempt. Use the same browser for consent and **Finish connecting**. The correlation window is ten minutes. If the confirmation page asks for local sign-in, sign in there with the same account. The page keeps **Finish connecting** available after sign-in.

| Where the attempt stops | Evidence to capture |
| --- | --- |
| Daikin's page before returning | HTTP status and the provider's request ID, if shown |
| Home Automation callback returns 400 | The fixed `Reason` from the backend's `ONECTA callback rejected` event |
| **Finish connecting** fails | The HTTP status and optional short `status` field from the local `/complete` response |

The backend logs `ONECTA callback accepted. Waiting for local confirmation.` when it has received the code and validated the browser correlation. That event precedes the token exchange, which starts with **Finish connecting**. A credential-free callback probe returns 400 by design. Restart the connection if its state has expired or was already used.

## Monitoring, quotas and manual requests

### Milestone 3D

The Heatpump card shows current and target leaving-water temperature, outdoor temperature, reported heating mode and power, control and setpoint modes, warnings, emergency status, and freshness. The card includes the last synchronization time and a 24-hour water-temperature chart after at least two samples are available. Readings are persisted by the existing MySQL telemetry writer under the existing gateway and management-point binding. No schema migration is needed. The [capability inventory](daikin-altherma-capabilities.md#milestone-3d-verification) records which fields were observed.

An authenticated live response identified the Altherma energy dataset as heating electricity in kWh. The bucket timing and accumulation rules remain unverified, so those arrays are excluded from telemetry and totals.

### Manual AC freshness and readback

- Only the designated ONECTA operator can submit an interactive command for an assigned AC in their property. Altherma and unidentified equipment are blocked in both the command handler and the HTTP adapter. The feature flag defaults to off.
- The backend accepts a successful, authenticated account-wide discovery response received within the last **two minutes**. It requests HTTP revalidation and rejects responses with a positive cache age for command purposes. The same device must be present and explicitly online. If the saved snapshot lacks an eligible receipt or the receipt is too old, the backend refreshes once and revalidates the target's current state and capabilities. An explicit invalid or stale device timestamp still blocks dispatch.
- `lastUpdateReceived`, when valid, remains the device observation time and retains the normal freshness requirement. `timestamp` is gateway metadata. The backend receipt field records when the successful provider response arrived and supports interactive commands when the device timestamp is absent. A database update never advances that receipt. This policy does not apply to automation or non-interactive dispatch.
- Power and heating/cooling use the advertised writable values. Temperature uses the active mode's writable range, step and verified Celsius unit. Fan control uses its advertised fixed-mode range. Temperature and fan requests carry the mode reviewed by the operator. A mode change before dispatch causes the request to be rejected.
- A command is audited as **Dispatched** before transport and moves to **Awaiting confirmation** only after ONECTA accepts it. A later eligible read can mark it **Confirmed** when it reports the requested value. The read must arrive after dispatch completed and carry a newer device timestamp when supplied. Temperature and fan also require the same mode. A missing or mismatched result remains open for normal polling. Definite rejection is **Failed**. A timeout or other ambiguous PATCH is **Uncertain** and blocks all commands on that device until matching readback. The application never replays a PATCH.
- With an accepted command awaiting confirmation, another device can be controlled. The same AC can receive a different independent control. An unresolved capability blocks another request for itself, and mode changes conflict with temperature and fan settings while either is unresolved. PATCHes and reads share the existing gate. Legacy pending and interrupted dispatched records remain device-wide blocks until reconciled.

### Debounced readback

Each accepted PATCH resets one account-wide timer to 30 seconds after that acceptance. At expiry, one discovery GET checks all devices and reconciles each open audit independently. If regular polling or another discovery already confirmed the scheduled commands, it cancels the extra GET. A partial read confirms only matching values. The normal 15-minute poll handles unresolved results, quota exhaustion, failed reads, or a backend restart during the in-memory window. The confirmation worker makes one scheduled attempt per window and uses the existing background quota rather than spending the interactive reserve.

### Request cost

The official public FAQ documents a rolling limit of 200 calls per day for private apps. The detailed Developer Portal contract requires an authenticated portal session. A supported multi-characteristic or multi-device write was not verified, so the adapter uses the existing characteristic-specific PATCH. The authenticated account-wide GET can reconcile several devices at once. [Daikin developer FAQ](https://www.daikin.eu/en_us/product-group/control-systems/daikin-developer-portal.html) · [ONECTA Developer Portal](https://developer.cloud.daikineurope.com/)

| Manual action | PATCH calls | GET calls |
| --- | --- | --- |
| Power on/off, apply temperature, change mode, or apply fixed fan speed | One for a changed value. Zero if the accepted snapshot already matches | Zero before dispatch with a recent eligible snapshot, otherwise one normal preflight. Up to one debounced GET per burst |
| Multiple independent changes to one device | One per changed characteristic after the previous PATCH is accepted | Share a recent snapshot when eligible. The burst shares one readback |
| Changes on several devices | One per changed characteristic and device | One eligible discovery can serve all preflights. The burst shares one readback |

Temperature +/- edits are local until **Apply target** submits the final selection. Power OFF is sent immediately once validated. The app counts reserved requests in MySQL and logs attempted GET, PATCH and token requests for each manual submission with an audit ID when available. A token refresh adds one request. Regular polling and preflight can replay a read once after an authorization failure. Confirmation leaves any failed GET to normal polling. The 15-minute worker interval is about 96 reads per day. At defaults, the background path stops at 160 calls and the command path at 158, leaving two background slots for readback or token refresh. Other explicit account operations consume the same rolling budget.

### Supervised AC verification

1. With writes off, open the assigned property in the local HTTPS app and refresh discovery if needed. Check the device name and assignment, current setting and mode, explicit reachability, advertised capability, absence of unresolved commands, and remaining quota. A 2026-11-22 live read found four assigned online ACs with power, heating/cooling and fixed fan. Sturgel Big Room and Sturgel Small room 2 also advertised a verified Celsius setpoint. Sturgel Kitchen advertised a writable range and step with no unit, so choose an approved power action there or use an assigned AC that displays temperature controls.
2. Obtain explicit approval for that specific device and action, with the exact requested value and an operator present to observe the unit. For temperature, pick one neighboring supported value from the currently reported target. Keep Altherma in monitoring mode.
3. From PowerShell, use a process-scoped opt-in for one backend. Stop the existing backend first to avoid duplicate polling. Keep the normal local HTTPS frontend, sign in as the existing operator, and use its Daikin device card.

   ```powershell
   $env:Daikin__EnableWrites = 'true'
   try {
     dotnet run --project src/backend/HomeAutomation.Api --no-launch-profile -- --urls http://localhost:5080 --environment Development
   } finally {
     Remove-Item Env:Daikin__EnableWrites -ErrorAction SilentlyContinue
   }
   ```

4. Submit the approved action once. Check the unit in person and wait for the debounced ONECTA readback. Check the command audit and request log. An uncertain result requires investigation before another action. Stop the test backend, remove the process override and restore the previous write configuration.

### General behavior

- Gateway discovery batches all management points. The adapter selects climate points and maps model/name, reachability, power, operating mode, room and outdoor temperature, current mode's target, humidity and available fan settings. Missing values remain unavailable. Periodized energy datasets need authenticated units, interval semantics and hardware verification before use. A unit's minimum heating setpoint comes from its active mode and advertised range; 10 °C support is unknown.
- Provider observation time, when supplied, is stored with telemetry and the database records receipt separately. The UI labels receipt time explicitly when the device timestamp is absent. Unknown or stale reachability never implies grid loss.
- The worker defaults to 15-minute reads and 35-minute freshness. A rolling MySQL budget counts discovery, refresh and command calls, with 40 of 200 slots reserved from background use. Each refresh retries a safe read at most once after an authorization failure. Throttling honors Retry-After when provided. Share a single app worker and avoid other integrations consuming the same quota. The Daikin worker is independent of Growatt and the simulator.
- OAuth tokens are encrypted using AES-GCM with owner-bound authenticated data and a separately configured key. No ONECTA password is collected. Keep the callback query out of proxy access logs. The app preserves its normal authentication and cookies.
- A grid outage policy, changes from 16 °C to a low setpoint, and arrival behavior remain future work. Use an independent grid or ATS signal and local frost protection. Loss of Internet or Daikin availability leaves the unit under its own control. Later automation must honor manual overrides and must not repeat a command whose outcome is unknown.
