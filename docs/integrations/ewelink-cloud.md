# eWeLink Cloud discovery and monitoring

The integration uses the CoolKit Developer OpenAPI with one locally designated account operator. Account access is working for the installed property. The adapter uses read-only device requests. [CoolKit OAuth API](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/OAuth2.0.md) · [eWeLink Developer Center](https://dev.ewelink.cc/)

## Verified contract

| Area | Home Automation behavior |
| --- | --- |
| Consent | Open the CoolKit authorization page with the documented HMAC-SHA256 signature. Authenticate and approve in the provider's browser UI. |
| Callback | Check the browser's secure correlation cookie, single-use state, and the original operator's property membership. Exchange the short-lived code immediately against an allowlisted host for the returned region. |
| Tokens | Use signed JSON for code exchange and refresh. Check expiration, keep rotated credentials encrypted using the existing ASP.NET Core Data Protection key ring, and bind them to the local owner and app ID. |
| Discovery | Read paged lists for every enumerated home and include the current home if its ID is separately reported. Deduplicate by stable provider identity, prefer owned copies, and skip groups. |
| Ingestion | Persist assigned relay states and reachability in existing telemetry. Keep verified numeric fields in the device's existing JSON snapshot and register supported capabilities as read-only. |
| Disconnect | The local UI removes stored token access and stops eWeLink sync while retaining assignments and history. Use eWeLink's own account controls if upstream access also needs revocation. |

## Milestone 4B discovery and measurements

A read-only inventory query on 2026-11-23 found five PSF-X67 devices and one THR320D. Those snapshots predate numeric mapping and have no raw device parameters or discovery totals. Thermostat Bidon is absent from the saved inventory. The revised scan covers a separately reported current home and recognizes owned and shared device rows regardless of model. CoolKit documents that brand and app entitlements can cause the reported total to exceed the returned rows. A short page with that discrepancy remains partial. The cause for this specific thermostat is unverified. [CoolKit Get Thing List](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/OAuth2.0.md#get-thing-list)

| Protocol | Snapshot keys | Interpretation and availability |
| --- | --- | --- |
| Thermostat UIIDs 15 and 181 | `params.currentTemperature`, `params.currentHumidity`, `params.sensorType` | Decimal strings in °C and % RH with no divider. The supported probe types determine which measurements can be read. Soil moisture and temperature-only probe values are excluded from relative humidity. THR320D temperature/humidity hardware is confirmed. Availability on this account's OAuth snapshot remains unverified. |
| Meter UIIDs 5 and 32 | `params.power`, `params.voltage`, `params.current` | Decimal strings already in W, V and A. PSF-X67 can use the UIID 32 profile when the API identifies it as such. Relay state, rated values, other UIIDs and numeric formats do not supply these readings. Availability on the installed units' OAuth snapshots remains unverified. |
| Energy | `oneKwhData`, `hundredDaysKwhData` in the UIID 5/32 protocol | CoolKit documents an interval counter and a separate device-request response for daily history. A verified passive daily counter or lifetime total is unavailable. Energy rows display Unavailable. The integration sends no activation or history commands and derives no energy from power snapshots. |

The adapter accepts online snapshots only. Existing readings remain visible as last known when fields are omitted, invalid, or stale. Unchanged snapshots retain the original receipt timestamp because the inventory API supplies no verified per-measurement timestamp. These readings produce no numeric history samples. The single-channel relay mapping, device assignments and ordering are preserved. [CoolKit UIID protocols](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/UIIDProtocol.md) · [Sonoff TH Elite](https://sonoff.tech/en-us/products/sonoff-th-elite-smart-temperature-and-humidity-monitoring-switch) · [Published device protocol mappings](https://github.com/AlexxIT/SonoffLAN/blob/master/custom_components/sonoff/core/devices.py)

Set `Ewelink:InventoryDiagnostics` to `true` locally to inspect the next normal poll. The logs include home counts, page totals, owned/shared/group counts, unsupported rows, UIIDs, known parameter names with their JSON types, and mapped capabilities. They omit names, IDs, values and arbitrary field names. Compare returned row counts with the mobile account and check whether the expected sensor or meter fields are present. If one device is still absent, CoolKit or the device vendor can confirm whether its brand and UIID are enabled for the Developer app. The setting adds no provider requests.

CoolKit's documented browser flow does not advertise PKCE. The app follows its signed authorization-code exchange and uses server-held credentials, short-lived browser-and-user state, HTTPS, and CSRF protection on local management actions. Signed requests follow CoolKit's [official OAuth Postman example](https://raw.githubusercontent.com/CoolKit-Technologies/eWeLink-API/main/media/files/CoolKit_OAuth2.0_Postman_Demo.zip). Restrict the key-ring directory and omit OAuth callback query strings from proxy access logs. The adapter has no provider device write operation and leaves the Sonoff rules under eWeLink's own control. The local CUBE API is a separate contract.

The local session cookie uses `SameSite=Strict`, so the browser omits it on the top-level return from CoolKit. The callback checks the registered HTTPS origin, OAuth state, expiry, and the `Secure; HttpOnly; SameSite=Lax` correlation cookie. It consumes the pending consent once, looks up its original owner, and rechecks that owner's current operator role and property access before exchanging the code. The original owner and property come from the server-side record. The callback saves successful exchanges in the existing encrypted token vault and returns to the property page with a success or failure marker. Local management requests still require a session and a CSRF token. [ASP.NET Core SameSite and antiforgery](https://learn.microsoft.com/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0)

CoolKit documents a 30-second code lifetime and error `405` for an expired code. The backend exchanges the code as soon as the callback is validated and sends it only once. The request uses the callback's supported regional host, `POST /v2/user/oauth/token`, the `X-CK-Appid` header, and a Base64 HMAC-SHA256 signature over the exact UTF-8 JSON bytes. The JSON contains `code`, the configured `redirectUrl` used during authorization, and `grantType` set to `authorization_code`. On a rejection the backend logs the HTTP status and numeric CoolKit error when available. The prior generic `rejected` response leaves the actual upstream reason unknown. If the next operator-initiated attempt fails, check those two numbers in the backend logs. [CoolKit OAuth and token exchange](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/OAuth2.0.md)

## Polling and limits

The default is one inventory scan every 30 minutes. `Ewelink:PollMinutes` accepts 15 through 1440. One running backend serializes the integration, spaces outbound Cloud requests by at least one second, and uses the same persisted cooldown for operator refresh and background work. A scan uses at most 12 discovery calls, including the family list. A truncated or brand-filtered result is shown as partial. Discovery failures retain the previous readings, and one safe read replay is allowed after refreshing an expired or rejected access token.

CoolKit publishes a free-app quota of 50,000 requests per region per month and an IP limit of 300 calls per five minutes. At the minimum interval, 12 reads per scheduled scan use at most 35,712 requests in a 31-day month for one continuously running installation before refresh or exceptional replay. Confirm the approved app's entitlement and any shared usage. HTTP 429 respects Retry-After. Provider error 412 identifies a quota limit, and bare HTTP 403 may indicate quota or denied access. A cooldown of at least a day protects those cases when the response carries no retry time. [CoolKit OAuth limits](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/OAuth2.0.md#requirements-of-calling-interface-important)

## Connect after approval

1. Confirm that the approved Personal Developer application can use OAuth, refresh, family discovery, and thing inventory in the account's region. Record the documented quota and the device/brand entitlement. Obtain its own App ID and app secret.
2. Register the exact HTTPS callback. On the existing Windows HTTPS development origin, use `https://ha-dev.britishlab.org:5443/api/v1/ewelink/callback` if CoolKit accepts that origin. Otherwise use an approved HTTPS origin that reaches the same Home Automation backend. Start from that same origin after registering it. Confirm the redirect registration and entitlement in the Developer Center before connecting.
3. Apply `004_ewelink_cloud.sql` through the existing migration runner with the scoped migration account and the dedicated Home Automation schema. Use the [Windows migration setup](../../README.md#apply-migrations-and-provision-a-viewer) with `--migrate` alone for an existing installation. The runtime verifies this migration only for eWeLink actions. Preserve the existing local Data Protection key ring.
4. From the repository root, set Development User Secrets. Supply the registered callback and an existing local operator email that has access to the intended property.

   ```powershell
   @{
       'Ewelink:AppId' = Read-Host 'Approved eWeLink App ID'
       'Ewelink:AppSecret' = [System.Net.NetworkCredential]::new('', (Read-Host 'eWeLink app secret' -AsSecureString)).Password
       'Ewelink:RedirectUri' = Read-Host 'Registered HTTPS callback URL'
       'Ewelink:OperatorEmail' = Read-Host 'Existing local operator email'
       'Ewelink:PollMinutes' = 30
   } | ConvertTo-Json -Compress | dotnet user-secrets set --project src/backend/HomeAutomation.Api
   ```

5. Restart the backend normally. On the registered HTTPS origin, sign in as the local operator. Select the intended property, open **eWeLink Cloud**, and use **Connect account**. Complete consent in the same browser. The backend completes the exchange on return. If the app prompts for local sign-in, sign in as the same operator to view the result. Review discovered devices before assigning them to properties and rooms. For a partial result, check the provider's brand coverage and pagination.
6. Compare reachability and relay state with the eWeLink app. Record sanitized UIIDs and parameter names needed to validate temperature, humidity, and automation status. Keep credentials, complete provider replies, and account identifiers out of diagnostics.
