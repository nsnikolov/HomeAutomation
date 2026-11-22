# eWeLink Cloud discovery and monitoring

Milestone 4A uses the CoolKit Developer OpenAPI with a single locally designated account operator. The Personal Developer application is awaiting approval. The implementation was verified with mocked responses. [CoolKit OAuth API](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/OAuth2.0.md) · [eWeLink Developer Center](https://dev.ewelink.cc/)

## Verified contract

| Area | Home Automation behavior |
| --- | --- |
| Consent | Open the CoolKit authorization page with the documented HMAC-SHA256 signature. Authenticate and approve in the provider's browser UI. |
| Callback | Require the existing local operator session, the same browser's secure correlation cookie, single-use state projected to the selected property, and its property membership. Exchange the short-lived code immediately against an allowlisted host for the returned region. |
| Tokens | Use signed JSON for code exchange and refresh. Check expiration, keep rotated credentials encrypted using the existing ASP.NET Core Data Protection key ring, and bind them to the local owner and app ID. |
| Discovery | Read the account's homes and paged device lists, deduplicate owned and shared devices by stable provider identity, and skip groups. A read-only account view exposes last sync, model, assignment, reachability, and available single-channel relay status. |
| Ingestion | Persist assigned relay states and reachability into existing provider bindings and MySQL telemetry, with all capabilities marked read-only. Snapshot receipts provide approximate observation times. Offline, absent, or malformed values preserve the last known reading without asserting a new measurement. |
| Disconnect | The local UI removes stored token access and stops eWeLink sync while retaining assignments and history. Use eWeLink's own account controls if upstream access also needs revocation. |

CoolKit's OAuth guide documents the single-channel `params.switch` values and says other status fields depend on the device protocol. Public documentation did not establish the API field paths, units, scaling, sentinel values, or report times for the installed temperature controllers. Temperature, humidity, automation mode, thresholds, and multi-channel relay state are therefore unavailable in this milestone. Model names do not grant a capability. After approval, obtain the relevant UIID-specific contract and record a sanitized observation that matches readings in the eWeLink app before mapping those fields. [CoolKit OAuth API](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/OAuth2.0.md) · [CoolKit V2 API](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/APICenterV2.md)

CoolKit's documented browser flow does not advertise PKCE. The app follows its signed authorization-code exchange and uses server-held credentials, short-lived browser-and-user state, HTTPS, and CSRF protection on local management actions. Signed requests follow CoolKit's [official OAuth Postman example](https://raw.githubusercontent.com/CoolKit-Technologies/eWeLink-API/main/media/files/CoolKit_OAuth2.0_Postman_Demo.zip). Restrict the key-ring directory and omit OAuth callback query strings from proxy access logs. The adapter has no provider device write operation and leaves the Sonoff rules under eWeLink's own control. The local CUBE API is a separate contract.

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

5. Restart the backend normally. On the registered HTTPS origin, sign in as the local operator. Select the intended property, open **eWeLink Cloud**, and use **Connect account**. Complete consent in the same browser. Review the discovered devices before assigning them to properties and rooms. For a partial result, check the provider's brand coverage and pagination rather than assuming absent devices are offline.
6. Compare reachability and relay state with the eWeLink app. Record sanitized UIIDs and parameter names needed to validate temperature, humidity, and automation status. Keep credentials, complete provider replies, and account identifiers out of diagnostics.
