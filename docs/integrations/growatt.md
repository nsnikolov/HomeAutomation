# Growatt read-only integration

## Decision

The simulator and Growatt dashboard panel use separate, property-scoped data sources. Milestone 2 supplied normalized read contracts, identity and telemetry mapping, polling, persistence and fixtures. Milestone 2B uses the Growatt International OpenAPI at `https://openapi.growatt.com` and the account token in backend User Secrets.

At this property ShinePhone shows two SPF 5000 ES inverters, Master and Slave, with approximately 4 kWp of PV, one shared 51.2 V / 200 Ah BMS-managed battery, and grid input through an external ATS. Generate the account token in ShineServer under Settings → Account Management and store it as `Growatt:ApiToken` in Development User Secrets or a backend secret source.

## Verified cloud access

- V1 plant discovery returned HTTP 200 with API code `0` and one plant. V1 device discovery returned HTTP 200 with API code `10011` and `error_permission_denied`. [Growatt V1 protocol](https://www.growatt.pl/wp-content/uploads/2020/01/Growatt-Server-Open-API-protocol-standards.pdf)
- V4 device discovery returned HTTP 200 with API code `0` and two distinct `storage` devices. The inventory response lists serials and loggers but includes no model or Master/Slave labels. [Growatt V4 regions](https://www.showdoc.com.cn/2540838290984246/11292913165257193) · [Device list](https://www.showdoc.com.cn/2540838290984246/11292915113214428) · [Device types](https://www.showdoc.com.cn/2540838290984246/11292914311318022)
- V4 `queryLastData` with `deviceType=storage` returned HTTP 200 and API code `0` with a record for each serial. Both records had the provider's `lost` flag set at the time of the check. V4 `queryDevicesHistoricalData` returned code `0` for a one-day request, with 287 timestamped records for one serial and zero for the other. The application collects history from future polls. The historical API was checked for access and coverage only. [Latest batch](https://www.showdoc.com.cn/2540838290984246/11292915898375566) · [Daily history batch](https://www.showdoc.com.cn/2540838290984246/11292916133591058) · [Storage status](https://www.showdoc.com.cn/2540838290984246/11292917862205677)
- Growatt documents one V4 device-list request per five seconds and one latest/history request per five minutes per interface for storage devices. This account also returned API code `102` during discovery and recovered after a five-minute gap. The adapter batches the pair and the worker defers throttling retries. [Device list limits](https://www.showdoc.com.cn/2540838290984246/11292915113214428) · [Error codes](https://www.showdoc.com.cn/2540838290984246/11292913883034530)

## Research and options

| Path | Evidence and limits |
| --- | --- |
| Official Growatt Cloud/OpenAPI | Growatt describes third-party API forwarding from its servers and its OSS manual describes installer-managed token access. The official channel is the preferred route once the regional host, authorization, read scope, SPF device coverage, schema and limits are confirmed. Public descriptions alone leave this account's entitlement and the required response contract unverified. [Growatt third-party integration overview](https://www.growatt.com/media/news/355) · [Growatt OSS installer manual](https://cdn.growatt.com/pdf/Installer%20Operation%20Manual.pdf) |
| ShineServer / ShinePhone | These vendor monitoring surfaces remain the operator reference for comparing readings and matching serials to the installed hardware. Home Assistant also documents the ShineServer account-manager token option. [Home Assistant Growatt documentation](https://www.home-assistant.io/integrations/growatt_server/) |
| Community libraries | The Python Growatt library has an OpenAPI client and model-specific mappings. Use it for comparison only after obtaining the official contract. Grott is a community datalogger parser and proxy, with protocol and firmware-specific coverage. It also has control functionality outside this project's read-only scope. Private app login or scraping is excluded from this implementation. [Growatt Python library documentation](https://github.com/indykoning/PyPi_GrowattServer/blob/master/docs/openapiv1.md) · [Grott maintainer documentation](https://github.com/johanmeijer/grott) |
| Supported local or vendor forwarding | Growatt describes serial collection and cloud forwarding in general. Its SPF product page identifies compatible monitoring hardware and PVkeeper for local commissioning. Treat those as leads. Obtain the supported logger, port, firmware-specific read contract and coexistence requirements for the actual installation. Keep parallel and BMS communications independent. [Growatt integration overview](https://www.growatt.com/media/news/355) · [SPF product and downloads](https://us.growatt.com/products/spf-3000-5000-es) |

## Enablement prerequisites

1. Run the count-only discovery command from the repository root. It reads `Growatt:ApiToken` via ASP.NET Core configuration and exits before starting the app, connecting to MySQL, or printing identifiers.

   ```powershell
   dotnet run --project src/backend/HomeAutomation.Api -- --discover-growatt
   ```

2. Use ShineServer and the inverter labels to match each serial to its physical Master or Slave role. Confirm which inverter reports the shared battery. The checked `time` values matched recent Europe/Sofia wall clock observations. The accompanying `calendar` value interpreted that wall clock as UTC+08:00 and appeared about six hours older. For this installation, configure `Europe/Sofia` and check the observation time against ShineServer during commissioning. If the API denies access, ask [Growatt regional support or the installer](https://en.growatt.com/support/contact) to confirm the account and device scope.
3. In Development User Secrets, set `Growatt:SiteKey` to the existing property key (the seed uses `countryside`). Set `Growatt:MasterId` and `Growatt:SlaveId` to the confirmed serials, `Growatt:BatterySourceId` to one of those serials, and `Growatt:TimeZone` to the verified IANA time zone. In deployment, use backend secret configuration for the token and double underscores in environment keys, for example `Growatt__SiteKey`. Review existing bindings before changing the serials or roles.
4. Use `Growatt:PollSeconds` in the range 300–1800 (default 300), `Growatt:TimeoutSeconds` shorter than the poll interval (default 15), and `Growatt:FreshnessSeconds` at least twice the poll interval and at most 86400 (default 900). Run a single polling instance for this account. Set `Growatt:Enabled` to true after confirming access, mapping and time zone, then restart the backend. It remains false by default. The worker checks both serials against the authorized V4 inventory before polling.

## Mapping and operating behavior

- Each SPF card shows one **PV1 voltage** row. A fresh dedicated `vpv1` reading takes priority when present, followed by the existing fresh `vpv` reading for this installation. Nighttime 0 V and 0 W are valid observations, and `ppv` continues to supply PV input power. The history responses had no `vpv1` field. `ipv` stayed at zero during generation on both inverters, and the vendor labels `iChargePV1` as PV1 charging current. PV input current is therefore unavailable on the cards. [Growatt V4 storage history fields](https://www.showdoc.com.cn/2540838290984246/11292918959841019)

- The cloud adapter maps per-inverter AC input active watts from `pAcInPut` when present in the scheduled V4 response. AC input behind the ATS, AC output to loads, and AC charging are separate quantities. The installation's utility grid import remains unavailable because the inverter readings lack source attribution and there is no verified utility meter total. [Growatt V4 storage latest fields](https://www.showdoc.com.cn/2540838290984246/11292918042831939) · [SPF active and apparent power registers](https://static.frigolit.net/files/Growatt_SPF5000_Modbus_Protocol_v0.14_2020-10-16.pdf#page=17)
- The adapter maps signed `pBat` when supplied by either inverter, using the cloud value in watts. The SPF sign convention is positive for discharging and negative for charging. Home Automation reverses that sign for its existing battery-flow display. Zero shows **Idle · 0 W**. The flow is shown on the reporting inverter and is excluded from shared battery power and installation totals. Shared voltage and SOC still come once from the nominated battery source. [Growatt V4 storage history fields](https://www.showdoc.com.cn/2540838290984246/11292918959841019) · [SPF signed battery power register](https://static.frigolit.net/files/Growatt_SPF5000_Modbus_Protocol_v0.14_2020-10-16.pdf#page=20)
- A dedicated battery endpoint accepts only the nominated source. Missing readings stay missing and prior readings age out. The installation prefers explicitly scoped totals. Optional `Growatt__SumIndependentPv` sums two fresh, equal-timestamp individual readings only when independently measured. Optional `Growatt__MasterReportsWholeSiteLoad` copies a confirmed whole-site master reading once. Both options default to false. Input voltage, inverter mode, loss of cloud and import power do not independently establish grid or ATS state.
- Use provider observation times for freshness and ordering. The adapter drops rows with missing, future or ambiguous local timestamps, and rejects duplicate or foreign serials. The existing MySQL writer records the wall time converted to UTC and receipt time separately. During provider outages the reader retains stored history and lets previously reported status age to unknown. Explicit device offline and fault reports remain distinct from provider failures.
- The worker settles one poll before scheduling another. Per-connection cadence, timeout, exponential recovery deferral and a delayed probe after repeated failures bound provider pressure. Server-provided retry delays take precedence. Simulator ingestion has its own worker, identities and telemetry source. Growatt uses existing MySQL tables and the capability model. No schema migration is needed for this milestone.

## SPF 5000 ES telemetry inventory

On 2026-11-23 a read-only MySQL query checked the latest persisted series for the `growatt` source by Master, Slave, shared battery and installation role. Both inverters had ten series with observations dated that day. The shared battery had two, and the installation had none. The V4 adapter makes one scheduled `queryLastData` request for both inverters and persists normalized readings from recognized fields. The audit observed the saved values through their mapped series. Existing logs and stored samples do not retain the raw field list, so they cannot establish the presence or absence of discarded fields. No extra provider requests were made.

The first V4 history audit read 263 Master and 264 Slave observations from the installed pair. Both contained `pAcInPut` and signed `pBat`. The generic fields `pCharge`, `pDischarge`, `pacToGrid` and `pacToUser` were zero throughout. A second history read confirmed negative `pBat` during AC charging and positive `pBat` while the battery discharge counter increased. All seven exactly paired timestamps reported distinct battery power between roles. The V1 storage latest request returned code `10011` (permission denied). The adapter uses cloud engineering values directly. The 0.1 W multiplier in the hardware protocol applies to raw registers. Scheduled latest-response availability is visible in the opt-in trace. [Growatt SPF Modbus protocol](https://static.frigolit.net/files/Growatt_SPF5000_Modbus_Protocol_v0.14_2020-10-16.pdf) · [V4 storage history fields](https://www.showdoc.com.cn/2540838290984246/11292918959841019)

| V4 storage field | Meaning and unit | Installed response evidence | Home Automation support |
| --- | --- | --- | --- |
| `outPutPower` | AC output active power delivered to loads, W | Stored for Master and Slave | `ActivePower`, displayed as Load/output power |
| `ppv` | Per-inverter PV input power, W | Stored for Master and Slave | `PvPower`, displayed in PV input |
| `vpv` | General PV input voltage, V | Daylight voltage and nighttime zero observed for both | Fallback in the PV1 voltage row, with fresh data preferred over stale data |
| `vpv1` | Channel-one voltage, V in the vendor's OpenAPI inverter contract | Absent from installed V4 history | Used when explicitly supplied, subject to freshness |
| `pAcCharge` | Reported AC charging power, W | Stored for both. Both latest readings were zero | `AcChargingPower`, displayed separately |
| `vGrid`, `freqGrid` | AC input voltage, V, and frequency, Hz | Stored for Master and Slave | `GridVoltage`, `GridFrequency`. No import or grid availability inference |
| `outPutVolt`, `loadPercent` | AC output voltage, V, and inverter load, % | Stored for Master and Slave | `AcOutputVoltage`, `InverterLoadPercent` |
| `vBat`, `capacity` | Battery voltage, V, and SOC, % | Stored for the shared battery from the configured Master. The adapter suppresses Slave battery samples | Displayed once on the shared battery |
| `warnCode` | Reported warning code, unitless | Stored for Master and Slave | Displayed |
| `spf5000StatusText` or `statusText` | Reported operating mode, text | Stored for Master and Slave. Normalization does not retain which field supplied the label | Displayed as operating mode |
| `lost`, `faultCode` or `errorCode` | Connectivity flag and fault/error code, unitless | Prior live checks confirmed `lost` for both. The telemetry inventory does not distinguish fault code fields | State and alerts, separate from numeric telemetry |
| `pAcInPut` | Inverter AC input active power, W | Observed on both inverters and differed from `pAcCharge` during positive input | `AcInputPower`, shown per inverter |
| `pBat` | Signed inverter-side battery power, W | Observed on both inverters with positive, negative and zero values | `BatteryPower`, sign normalized and shown per inverter |
| `pCharge`, `pDischarge` | Generic storage charge and discharge watts | Both stayed at zero throughout the SPF history sample | Unmapped |
| Utility grid import and shared battery net power | Property-wide measurements, W | No verified whole-site source | Unavailable |
| Unverified | AC input/output apparent power, VA | No stored series. Vendor documents distinct hardware quantities | Unmapped |
| `ipv`, `iChargePV1` | Documented PV input current and PV1 charging current | `ipv` was zero during production. `iChargePV1` varied and is scoped to charging | PV input current unavailable |
| Unverified | Inverter and DC-DC temperatures, °C | No stored series. Cloud sensor names and scaling remain unverified | Unmapped |
| Unverified | Daily energy counters, kWh | No stored series. The SPF local protocol lists separate PV, AC charge and discharge counters. Cloud field names, scope and reset timing remain unverified | Unmapped |

## Viewing live monitoring locally

Set `Growatt:TelemetryDiagnostics` to `true` in local User Secrets and restart the backend to inspect the normal latest response. Each existing poll logs the Master or Slave role, the presence or JSON type of allowlisted fields, and whether the adapter mapped the power values. The trace omits serial numbers, tokens, measurement values, and raw responses. Turn the setting off after inspection.

Start the backend and frontend using the [Windows development guide](../../README.md). Open http://localhost:5173/, sign in with the provisioned viewer, select **Countryside House**, and choose **Live Growatt** from the sidebar. **Dashboard** opens the simulator. **Live Growatt** shows source timestamps and freshness per device, synchronization time for the current backend process, and recent Growatt alerts. Polling runs in the backend independently of either page.

The confirmed physical roles are configured privately in User Secrets. Both inverters reported 32% in the commissioning batch, and the Master reported 52.09 V. The Master is the single source for shared SOC and voltage. The commissioning batch also returned recent readings with both provider offline flags set. Treat that label as Growatt's reported connectivity and check observation age and ShineServer during diagnosis.

The first continuous poll persisted separate Master and Slave readings and one shared battery in MySQL 8.0.46 while the browser was closed. Both inverters reported AC charge and bypass mode. The Master reported 41 W of inverter-side AC charging and supplied the shared battery reading of 32% and 52.06 V. The database also showed fresh simulator readings and no Growatt installation totals.

## Low-battery shutdown and uneven recovery

For a suspected outage, examine the timestamps for falling SOC, any reported warnings or faults, and the last observation from each inverter. During recovery, the live view highlights when Growatt reports Master online and Slave offline. Existing telemetry and device alerts keep their timestamps. This pattern can guide a local inspection of the Slave after grid restoration. Confirmation of the full sequence requires independent grid or ATS readings and a documented low-battery indicator. The dashboard has read-only monitoring.
