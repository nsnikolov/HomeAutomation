# eWeLink Cloud discovery and monitoring

The integration uses the CoolKit Developer OpenAPI with one locally designated account operator. Account access is working for the installed property. Monitoring is read-only by default. [CoolKit OAuth API](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/OAuth2.0.md) · [eWeLink Developer Center](https://dev.ewelink.cc/)

## Verified contract

| Area | Home Automation behavior |
| --- | --- |
| Consent | Open the CoolKit authorization page with the documented HMAC-SHA256 signature. Authenticate and approve in the provider's browser UI. |
| Callback | Check the browser's secure correlation cookie, single-use state, and the original operator's property membership. Exchange the short-lived code immediately against an allowlisted host for the returned region. |
| Tokens | Use signed JSON for code exchange and refresh. Check expiration, keep rotated credentials encrypted using the existing ASP.NET Core Data Protection key ring, and bind them to the local owner and app ID. |
| Discovery | Read paged lists for every enumerated home and include the current home if its ID is separately reported. Deduplicate by stable provider identity, prefer owned copies, and skip groups. |
| Ingestion | Persist assigned relay states and reachability in existing telemetry. Keep verified numeric fields in the device's existing JSON snapshot and register supported capabilities as read-only. |
| Disconnect | The local UI removes stored token access and stops eWeLink sync while retaining assignments and history. Use eWeLink's own account controls if upstream access also needs revocation. |

## Discovery and capability-based measurements

A read-only inventory query on 2026-11-23 found five PSF-X67 devices and one THR320D. The latest normalized snapshots contained power, voltage and current for all five PSF-X67 devices and 12.9 °C and 100% RH for THR320D. Each device also had relay support. The account reported a partial inventory, and Thermostat Bidon was absent. The scan covers the enumerated families and any separately reported current home, and accepts owned and shared device records with a usable identity regardless of UIID. CoolKit documents that its brand and app entitlements can cause reported totals to exceed returned rows. Saved snapshots omit the original page counts and rejected rows, so the thermostat's specific cause remains unverified. [CoolKit Get Thing List](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/OAuth2.0.md#get-thing-list)

| Protocol | Snapshot keys | Interpretation and availability |
| --- | --- | --- |
| Thermostat UIIDs 15 and 181 | `params.currentTemperature`, `params.currentHumidity`, `params.sensorType` | Decimal strings in °C and % RH with no divider. Exact THR320D and THR316D models can select the thermostat parser if UIID is omitted. Known probe metadata preserves supported rows when a value is missing. Humidity requires a relative-humidity probe, which excludes soil moisture and temperature-only probes. |
| Meter UIIDs 5 and 32 | `params.power`, `params.voltage`, `params.current` | Decimal strings already in W, V and A. Each row requires an actual recognized measurement. A previously verified reading can remain visible as last known. |
| Energy | `oneKwhData`, `hundredDaysKwhData` in the UIID 5/32 protocol | CoolKit documents an interval counter and a separate device-request response for daily history. A verified passive daily counter or lifetime total is unavailable for this account, so its cards omit energy rows. |

The adapter reads numeric values from online snapshots. Cards show only supported rows and distinguish Unavailable from last known values. Unchanged values keep their original receipt time because the inventory API supplies no verified per-measurement timestamp. The adapter writes no numeric history from these snapshots. The existing inventory poll already supplied the installed THR320D values, so a dedicated status call is unnecessary. For an account that omits those fields, CoolKit documents a separate authorized GET `/v2/device/thing/status` that can request `currentTemperature`, `currentHumidity` and `sensorType` for a discovered device. Coverage through that endpoint would need separate verification; it is outside ordinary polling. [CoolKit UIID protocols](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/UIIDProtocol.md) · [Sonoff TH Elite](https://sonoff.tech/en-us/products/sonoff-th-elite-smart-temperature-and-humidity-monitoring-switch) · [CoolKit device status](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/OAuth2.0.md#get-device-or-group-status)

Set `Ewelink:InventoryDiagnostics` to `true` locally for a normal poll. The logs report home coverage, page totals, reasons for rejected rows and pagination stops, duplicate counts, allowlisted models, UIIDs, known parameter names and JSON types, and supported capabilities. Unknown model strings are grouped as `other`. Device names, IDs, values and arbitrary field names are omitted. Compare the reported totals with received counts to distinguish provider omissions from local filtering. If CoolKit omits a device, confirm its family, brand and app entitlement with the vendor. The setting adds no provider requests.

## Passive WebSocket telemetry

Enable the listener on the backend with `Ewelink:EnableWebSocket=true`. It is off by default and uses the existing OAuth connection. In Windows development, run the following and restart the backend.

```powershell
dotnet user-secrets set "Ewelink:EnableWebSocket" "true" --project src/backend/HomeAutomation.Api
```

Allow the next scheduled sync or use **Force Refresh** when available. The listener obtains the account key from the OAuth family listing, stores it in the existing encrypted token envelope, and uses the regional CoolKit dispatch service and authenticated `userOnline` handshake with standard TLS certificate validation. Dispatch counts against the existing request budget and respects provider cooldowns. The listener holds one connection for the linked account in the backend process. It reconnects with bounded backoff and uses the token refreshed by scheduled HTTP synchronization. The five-minute poll and Force Refresh remain available. [CoolKit OAuth API and WebSocket handshake](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/OAuth2.0.md#real-time-control-device)

The app accepts recent timestamped device reports for assigned, owned PSF-X67 (UIID 32) and THR320D (UIID 181) devices. It keeps unaffected fields intact and rejects older or duplicate values per field. Verified observations are written through the existing telemetry store and delivered to the assigned property via SignalR. Cards display the reading time and mark recent push data **Live**. Older push data appears as **Last known**. Electrical readings returned by ordinary HTTP discovery appear as cloud snapshots. Relay push reports are for monitoring. The existing HTTP readback and audit pipeline continue to confirm manual commands.

CoolKit describes UIID 32 power monitoring using the UIID 5 reporting behavior. That protocol publishes changes in watts, volts and amps during a device activation window initiated by the `uiActive` command. Passive power, voltage and current updates therefore depend on whether the device is already reporting and CoolKit forwards those reports to this OAuth app. With live electrical activation off, the listener sends the account handshake and heartbeat only. [CoolKit UIID 5 and 32 protocols](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/UIIDProtocol.md#uiid32-power-detection-plug-overload-alarm)

On 2026-11-23, the existing developer APPID and saved OAuth grant passed regional dispatch, the TLS upgrade and `userOnline` authentication. A 70-second probe window produced zero eligible PSF-X67 electrical reports and zero eligible THR320D temperature reports. Live measurement delivery on this account remains unverified.

If the feed is quiet, check for **eWeLink WebSocket authenticated** after the next successful inventory sync. A deferred connection logs only the stage outcome, HTTP status, and CoolKit code where available. Check the APPID entitlement with CoolKit if dispatch or authentication is rejected. If authentication succeeds and only snapshot readings appear, the account may be receiving no eligible timestamped device reports. Set `Ewelink:EnableWebSocket` to `false` and restart to use HTTP polling alone.

### Operator-viewed electrical reporting

Live electrical activation defaults to off. With `Ewelink:EnableWebSocket` enabled, an operator can also enable reporting activation and restart the backend.

```powershell
dotnet user-secrets set "Ewelink:EnableLiveElectricalReporting" "true" --project src/backend/HomeAutomation.Api
```

Open the eWeLink page as the linked account's authorized operator. The backend sends only `{"uiActive":60}` to eligible owned, assigned and recently seen online PSF-X67 devices (UIID 32). It reuses the account socket, limits activation to eight meters and renews each meter approximately every 50 seconds. Visible tabs renew a shared server-side viewing lease every 10 seconds. Leaving or hiding the page releases that tab's lease. If the stop message is lost, the lease expires after 25 seconds and any existing device activation expires naturally. Provider commands are paced and charged to the shared regional budget. The last 5,000 requests in that budget are reserved for existing operations, including HTTP reconciliation. Five-minute polling, manual relay permissions and inverter protection stay in place. [CoolKit activation parameters](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/UIIDProtocol.md#uiid5-power-detection-single-channel-plug) · [CoolKit WebSocket update and acknowledgement](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/OAuth2.0.md#websocket-update-device-status)

An accepted activation can remain **Waiting for a measurement change**. CoolKit documents change thresholds of 2 W, 5 V and 0.03 A. A fresh timestamped report updates the applicable rows and leaves the other rows intact. Cached electrical snapshots appear as last known. If CoolKit rejects an activation, the card and backend log show the provider code and activation stops for the backend process. An unacknowledged command also stops activation. Review the result before restarting with activation enabled. Setting `Ewelink:EnableLiveElectricalReporting` to `false` leaves passive WebSocket telemetry available. The activation path was verified with mock responses. Live acceptance for the installed meters remains unverified.

### Trace electrical reports

For a temporary local trace, set `$env:Ewelink__WebSocketDiagnostics = 'true'` in the PowerShell session that launches the backend and restart it. Open the eWeLink page as the linked operator, append `?ewelinkDiagnostics=1` to its URL, and keep it visible across at least one renewal. Compare the backend's **eWeLink WebSocket trace** summaries with **eWeLink SignalR trace** and **eWeLink React trace** in the browser console. These summaries contain counts, recognized field names and JSON types, and fixed reason codes. They omit credentials, device and property identities, measurements and timestamps.

| Read the counters in order | Interpretation |
| --- | --- |
| `activationAccepted` → `electricalFrames` | CoolKit acknowledged activation. Electrical frames count payloads with documented `power`, `voltage` or `current` keys before application filtering. Update frames without those keys are counted separately under `updateFrames.params.*`. Compare at least one interval that includes actual load changes. |
| `queuedElectricalFrames` → `matchedInventory` → `mappedElectricalSamples` | The trace identifies rejected senders, account keys, identities, formats, timestamps and property checks. Duplicate and out-of-order readings are counted per electrical field. Electrical reports are ordered by provider observation time independently of undated HTTP snapshots. |
| `persistedElectricalSamples` → `cardSnapshotsSaved` → `signalRSent` | History and the card snapshot have separate write outcomes. The hub sends a property-scoped invalidation after both writes. A completed hub send is recorded independently of browser receipt. |
| `electricalNotices` → `refreshes` → `liveRows` | The browser saw a telemetry invalidation, refreshed the eWeLink query and rendered fresh electrical rows. Other eWeLink notices include activation status changes. |

Remove the query parameter to stop browser tracing. Remove the environment override with `Remove-Item Env:Ewelink__WebSocketDiagnostics` and restart the backend to stop server tracing.

CoolKit describes `beginIndex` as an item sequence cursor and `total` as the number of things, including groups. The documentation does not define a short page as the end of the inventory. When the known total exceeds the number of unique indexes seen, the adapter can follow an advancing cursor from a short page. It retains the last valid total if a later page omits it. Pagination stops when the total is reached, the page is empty, the cursor cannot advance, a short page lacks a known total, or the request budget is spent. Until a later normal poll returns a usable seventh device, the missing thermostat's availability through CoolKit remains unverified. [CoolKit Get Thing List](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/OAuth2.0.md#get-thing-list)

CoolKit's documented browser flow does not advertise PKCE. The app follows its signed authorization-code exchange and uses server-held credentials, short-lived browser-and-user state, HTTPS, and CSRF protection on local management actions. Signed requests follow CoolKit's [official OAuth Postman example](https://raw.githubusercontent.com/CoolKit-Technologies/eWeLink-API/main/media/files/CoolKit_OAuth2.0_Postman_Demo.zip). Restrict the key-ring directory and omit OAuth callback query strings from proxy access logs. The local CUBE API is a separate contract.

The local session cookie uses `SameSite=Strict`, so the browser omits it on the top-level return from CoolKit. The callback checks the registered HTTPS origin, OAuth state, expiry, and the `Secure; HttpOnly; SameSite=Lax` correlation cookie. It consumes the pending consent once, looks up its original owner, and rechecks that owner's current operator role and property access before exchanging the code. The original owner and property come from the server-side record. The callback saves successful exchanges in the existing encrypted token vault and returns to the property page with a success or failure marker. Local management requests still require a session and a CSRF token. [ASP.NET Core SameSite and antiforgery](https://learn.microsoft.com/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0)

CoolKit documents a 30-second code lifetime and error `405` for an expired code. The backend exchanges the code as soon as the callback is validated and sends it only once. The request uses the callback's supported regional host, `POST /v2/user/oauth/token`, the `X-CK-Appid` header, and a Base64 HMAC-SHA256 signature over the exact UTF-8 JSON bytes. The JSON contains `code`, the configured `redirectUrl` used during authorization, and `grantType` set to `authorization_code`. On a rejection the backend logs the HTTP status and numeric CoolKit error when available. The prior generic `rejected` response leaves the actual upstream reason unknown. If the next operator-initiated attempt fails, check those two numbers in the backend logs. [CoolKit OAuth and token exchange](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/OAuth2.0.md)

## Polling and limits

The default sync interval is five minutes. `Ewelink:PollMinutes` accepts 5 through 1440. One running backend serializes the integration and spaces outbound Cloud requests by at least one second. A scan uses at most 12 discovery calls, including the family list. A truncated or brand-filtered result is shown as partial. Discovery failures retain the previous readings and back off for at least 30 minutes, or longer when the configured interval or provider cooldown requires it. One safe read replay is allowed after refreshing an expired or rejected access token.

**Force Refresh** runs the same provider discovery and ingestion immediately if only the normal schedule is pending. It requires the account operator, access to the selected property, and CSRF validation. A running synchronization, an attempt in the past minute, a provider cooldown, or exhausted quota defers the request. The page reports its actual outcome and shows when the last scan started and last succeeded. Inventory diagnostics also run for forced scans when enabled.

CoolKit publishes a free-app quota of 50,000 requests per region per month and an IP limit of 300 calls per five minutes. At five minutes, an account that uses all 12 discovery calls per scan could exceed the monthly quota. Home Automation persists regional request counts in UTC day buckets covering at least 31 days and stops at 50,000. It counts token calls and replays and starts tracking when migration 007 is applied. Usage from other applications or before the migration remains subject to CoolKit's upstream quota. The integration honors HTTP 429 Retry-After and enforces a day-long default cooldown for provider error 412 or bare HTTP 403 when no retry time is supplied. [CoolKit OAuth limits](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/OAuth2.0.md#requirements-of-calling-interface-important)

An existing User Secrets or environment override still sets the effective polling interval. In a local Development backend, update or remove the User Secrets override and restart the backend.

```powershell
dotnet user-secrets set 'Ewelink:PollMinutes' '5' --project src/backend/HomeAutomation.Api
```

The environment variable `Ewelink__PollMinutes` takes precedence over User Secrets. Update it in the backend launcher or process environment, or remove it to use the underlying configuration.

## Manual relay control

Apply migrations 008 and 009 with the existing migration runner using the scoped migration account. Complete a normal inventory sync so the device's stored snapshot includes its UIID. The integration accepts owned, assigned PSF-X67 devices reporting UIID 32 and a single-channel relay. The THR320D remains read-only because no matching UIID 181 write contract was verified. Autonomous thermostat rules remain under eWeLink's control. [CoolKit device status](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/OAuth2.0.md#update-the-status-of-a-device-or-group) · [CoolKit UIID protocols](https://github.com/CoolKit-Technologies/eWeLink-API/blob/main/en/UIIDProtocol.md)

Both permission switches default to off. Enable the global setting in backend configuration, then restart the backend.

```powershell
dotnet user-secrets set 'Ewelink:EnableWrites' 'true' --project src/backend/HomeAutomation.Api
```

As the eWeLink operator, open the assigned property and select **Enable relay controls** on each eligible device you want to control. This stores a preference by internal device ID and reveals ON/OFF on that card. **Disable relay controls** revokes the opt-in. Existing `Ewelink:RelayWriteDevices` configuration entries are used for devices without a saved preference. A saved UI preference takes precedence, while `Ewelink:EnableWrites=false` disables all writes.

Inverter In and Inverter out are protected. The confirmation requirement follows their internal records after rename. Each command to a protected relay requires explicit confirmation. Authorized operators can select ON or OFF when the relay state is fresh and the device is online. Each command is audited with the local actor, property, internal device ID, requested state, and outcome.

A command sends one signed-in device status POST using the OAuth bearer token. CoolKit acceptance starts a bounded targeted status GET. The app shows Pending while it waits, Confirmed when the returned state matches, Failed after a definitive rejection, and Unverified when the result is ambiguous or readback fails or disagrees. A returned relay state updates the card and telemetry. An unresolved command blocks further commands until normal discovery observes the relay again. The shared request budget, pacing and cooldown apply to both calls.

## Connect after approval

1. Confirm that the approved Personal Developer application can use OAuth, refresh, family discovery, and thing inventory in the account's region. Record the documented quota and the device/brand entitlement. Obtain its own App ID and app secret.
2. Register the exact HTTPS callback. On the existing Windows HTTPS development origin, use `https://ha-dev.britishlab.org:5443/api/v1/ewelink/callback` if CoolKit accepts that origin. Otherwise use an approved HTTPS origin that reaches the same Home Automation backend. Start from that same origin after registering it. Confirm the redirect registration and entitlement in the Developer Center before connecting.
3. Apply the repository migrations through the existing runner with the scoped migration account and dedicated Home Automation schema. eWeLink requires migrations 004, 007, 008 and 009. Use the [Windows migration setup](../../README.md#apply-migrations-and-provision-a-viewer) with `--migrate` alone for an existing installation. Preserve the existing local Data Protection key ring.
4. From the repository root, set Development User Secrets. Supply the registered callback and an existing local operator email that has access to the intended property.

   ```powershell
   @{
       'Ewelink:AppId' = Read-Host 'Approved eWeLink App ID'
       'Ewelink:AppSecret' = [System.Net.NetworkCredential]::new('', (Read-Host 'eWeLink app secret' -AsSecureString)).Password
       'Ewelink:RedirectUri' = Read-Host 'Registered HTTPS callback URL'
       'Ewelink:OperatorEmail' = Read-Host 'Existing local operator email'
       'Ewelink:PollMinutes' = 5
   } | ConvertTo-Json -Compress | dotnet user-secrets set --project src/backend/HomeAutomation.Api
   ```

5. Restart the backend normally. On the registered HTTPS origin, sign in as the local operator. Select the intended property, open **eWeLink Cloud**, and use **Connect account**. Complete consent in the same browser. The backend completes the exchange on return. If the app prompts for local sign-in, sign in as the same operator to view the result. Review discovered devices before assigning them to properties and rooms. For a partial result, check the provider's brand coverage and pagination.
6. Compare reachability and relay state with the eWeLink app. Record sanitized UIIDs and parameter names needed to validate temperature, humidity, and automation status. Keep credentials, complete provider replies, and account identifiers out of diagnostics.
