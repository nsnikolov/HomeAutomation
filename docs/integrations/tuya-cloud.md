# Smart Life / Tuya Cloud monitoring

Milestone 5A reads a single linked Smart Life account through its Tuya Smart Home cloud project. Home Automation keeps project credentials on the backend and holds project tokens in memory. All provider calls are GETs. Property and room assignments are local to Home Automation. [Tuya authentication](https://developer.tuya.com/en/docs/iot/authentication-method?id=Ka49gbaxjygox) · [Tuya request signing](https://developer.tuya.com/en/docs/iot/new-singnature?id=Kbw0q34cs2e5g)

## Connect an approved project

1. In Smart Life, check **Me → Settings → Account and Security → Region**. In the [Tuya Developer Platform](https://platform.tuya.com/), open **Cloud → Development** and create or open a **Smart Home** project in a data center that serves that account. Verify that **Authorization**, **Get User’s Device List**, and **Get the specifications and properties of the device** are subscribed and authorized for the project. The service names shown in the project wizard can vary. This integration uses the HTTP APIs and needs no message subscription or scene authorization. [Smart Home project setup](https://developer.tuya.com/en/docs/iot/Platform_Configuration_smarthome?id=Kamcgamwoevrx) · [IoT Core API list](https://developer.tuya.com/en/docs/cloud/device-connection-service?id=Kb0b8geg6o761)
2. In the project, open **Devices → Link Tuya App Account → Add App Account**. Scan the QR code with the Smart Life app while signed in to the intended account, confirm the link in the app, and select the device linking method that exposes the intended devices. Check **All Devices** and copy the **UID** from the linked account row. Copy the **Access ID** and **Access Secret** from the project's **Overview → Authorization Key**. [Tuya account linking](https://developer.tuya.com/en/docs/iot/link-devices?id=Ka471nu1sfmkl) · [Tuya UID field](https://images.tuyacn.com/goat/pdf/1718345734735/Configuration%20Wizard%20of%20Smart%20Home%20PaaS_Tuya%20Developer%20Platform_Tuya%20Developer%20Platform.pdf)
3. Apply additive migration `005_tuya_cloud.sql` through the existing scoped migration account, running `dotnet run --project src/backend/HomeAutomation.Api -- --migrate` as described in the [Windows migration setup](../../README.md#apply-migrations-and-provision-a-viewer). Run it against the dedicated Home Automation schema. Tuya runtime checks the migration without applying it.
4. From the repository root, set Development User Secrets. Use an existing local operator with access to the properties the account will serve. Production should use the deployment secret store.

   ```powershell
   @{
       'Tuya:AccessId' = Read-Host 'Tuya project Access ID'
       'Tuya:AccessSecret' = [System.Net.NetworkCredential]::new('', (Read-Host 'Tuya project Access Secret' -AsSecureString)).Password
       'Tuya:UserId' = Read-Host 'UID from the linked Smart Life account row'
       'Tuya:DataCenter' = Read-Host 'Data center key from the table below'
       'Tuya:OperatorEmail' = Read-Host 'Existing local operator email'
       'Tuya:PollMinutes' = 30
   } | ConvertTo-Json -Compress | dotnet user-secrets set --project src/backend/HomeAutomation.Api
   ```

5. Restart the backend, sign in as the operator, and open the intended property’s **Smart Life / Tuya** panel. The initial sync runs in the background. Review the discovered names and models, then assign devices to the intended property and room. A device already assigned to one property cannot be reassigned from a different property. Verify read-only values against Smart Life. Keep access credentials, local keys, account identifiers, and complete provider payloads out of diagnostics.

| `Tuya:DataCenter` | Official API host |
| --- | --- |
| `central-europe` | `openapi.tuyaeu.com` |
| `western-europe` | `openapi-weaz.tuyaeu.com` |
| `western-america` | `openapi.tuyaus.com` |
| `eastern-america` | `openapi-ueaz.tuyaus.com` |
| `china` | `openapi.tuyacn.com` |
| `india` | `openapi.tuyain.com` |
| `singapore` | `openapi-sg.iotbing.com` |

The configured data center selects a fixed HTTPS endpoint. Compare the project and account region when the device list is empty. [Tuya API endpoints](https://developer.tuya.com/en/docs/iot/api-request?id=Ka4a8uuo1j4t4)

## Discovery and measurements

- The backend obtains a simple-mode project token with `GET /v1.0/token?grant_type=1`, renews it before expiry, and allows one replay of a read after a cached token is rejected. It reads the linked UID's inventory and status through `GET /v1.0/users/{uid}/devices` and uses the device-specific `GET /v1.0/iot-03/devices/{id}/specification` status definitions. Function definitions never grant a monitoring capability by themselves. [Token API](https://developer.tuya.com/en/docs/cloud/6c1636a9bd?id=Ka7kjumkoa53v) · [User device list](https://developer.tuya.com/en/docs/cloud/ad2823ae46?id=Kconjtzq1vk1q) · [Device specifications](https://developer.tuya.com/en/docs/cloud/68c2e82f73?id=Kag2ybtxwlb9w)
- Verified combinations include Boolean relay states, temperature in Celsius or explicitly convertible Fahrenheit, relative humidity, battery percentage, and voltage, current, and power. Separate relay codes retain separate endpoints. Numeric status uses its device-declared integer scale and unit. Unsupported codes, missing units, and inconsistent data remain unavailable. A battery enum has no inferred percentage. Interval energy is excluded until the device-specific reporting window and accumulation semantics are verified. [Temperature/humidity status set](https://developer.tuya.com/en/docs/iot/s?id=K9gf48k1c0sgo) · [Socket datapoints](https://developer.tuya.com/en/docs/iot/product-standard-function-introduction?id=K9tp15ceh63gr)
- Tuya's `update_time` is the device status update timestamp. It is kept separate from receipt time and is a device-level age hint. It does not establish when each datapoint was sampled. Undated readings can be displayed with their time unavailable and are excluded from persisted history. Offline, absent, or malformed data retains last known readings and ages naturally. A complete inventory that omits a device marks it stale, while partial results preserve the prior inventory. Verify paging, per-device codes, units and reporting cadence with the linked account before relying on these readings.

## Pricing, trial expiry, and service limits

Tuya publishes a Trial Edition for individual development and debugging with a monthly resource pack of 26,000 API calls and 68,000 messages, one data center, and up to 50 devices. The trial does not allow overage or commercial use. Paid plans have subscription and region-specific usage charges. The actual plan, trial expiry, included services and renewal terms are shown under the project's subscriptions in the portal. Check those dates before onboarding or renewal. Monthly quota refresh does not renew an expired subscription. [Tuya Cloud pricing](https://developer.tuya.com/en/docs/iot/membership-service?id=K9m8k45jwvg9j)

The backend defaults to a 30-minute minimum interval. It spaces requests by at least one second in one process. A scan reads up to six inventory pages of 25 items and fetches up to five pending specifications. Successful specifications are cached for a day. At the default interval, this at-most-11-read scan would use up to 16,368 reads over 31 days before token calls and exceptional replays. Manual refresh shares the schedule. Apply the approved project's actual limits and account for other consumers. [Tuya frequency controls](https://developer.tuya.com/en/docs/iot/frequency-control?id=Kcojz2r2dg1f6)

HTTP 429 and Tuya error 1199 defer reads, honoring any Retry-After. Expired plans or API subscriptions and exhausted quota have separate statuses and pause attempts for at least a day. Access failures defer attempts for six hours. The operator panel shows the provider code when available. Production logs contain the sanitized status and code. [Tuya error codes](https://developer.tuya.com/en/docs/iot/error-code?id=K989ruxx88swc)
