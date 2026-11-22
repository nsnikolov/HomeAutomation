# Daikin Altherma ONECTA capabilities

## Observation

On 2026-11-22, one authenticated `GET /v1/gateway-devices` returned HTTP 200. The probe resolved **Heatpump** from the existing Home Automation binding and matched both its saved gateway ID and management point ID before inspecting the response. The request used the app's protected token store, existing typed client and quota ledger. The local ledger recorded one request against Daikin's 200-call sliding-day allowance. The payload was inspected in memory. The inventory omits account, network and device identifiers and personal schedule values. [Daikin Developer Portal and quota](https://www.daikin.eu/en_us/product-group/control-systems/daikin-developer-portal.html)

The response describes one `heating-wlan` gateway with five management points. Its primary `climateControl` point has subtype `mainZone` and matches the existing binding. The remaining points are `gateway`, `indoorUnitHydro`, `outdoorUnit` and `userInterface`. These observations apply to this device, configuration and response. Capability absence here does not establish availability on other Altherma installations.

**R** means the value was readable in this response. **R/W advertised** means the characteristic or enclosing object supplied `settable: true`. The inspection performed only the GET, so write permission and dispatch behavior remain untested. A number in a temperature row is a JSON number. Temperature units below retain the provider's literal `C`, suitable for display as °C. Range metadata is stated as minimum, maximum and step.

The path prefix `G` represents the matching gateway object in the returned array. `M` represents `G.managementPoints[1]`, its primary main zone. Indices are the order of this response. Application identity follows gateway and point IDs, which remain private.

## Main zone and gateway

| Provider field path | Description | Example and data type | Unit | Readable / writable | Currently mapped | Recommended mapping | Confidence |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `G._id`, `M.embeddedId` | Gateway and management point identity | Strings withheld | — | R, identity | Yes, combined stable binding | Keep private and use both when resolving the zone | Both matched the saved binding |
| `G.id`, `G.embeddedId` | Additional gateway identifiers | Strings withheld | — | R, identifier metadata | No | Keep out of telemetry and use the established binding | Present, semantics beyond identity unverified |
| `G.type` | Gateway type | String `heating-wlan` | — | R, type metadata | No | Keep as provider inventory metadata | Observed |
| `G.deviceModel` | Gateway model metadata | String withheld | — | R, metadata | Yes, model | Keep as inventory metadata | Present |
| `G.isCloudConnectionUp.value` | ONECTA gateway reachability | Boolean `true` | — | R, `settable: false` | Yes, connectivity | Continue mapping as cloud reachability | Observed, physical operation requires other evidence |
| `G.timestamp` | Gateway timestamp field | String, content withheld | — | R, metadata | No. The mapper looks for `lastUpdateReceived` | Validate timestamp format, timezone and meaning before using for freshness | Field observed, semantics unverified. The adapter used receipt time for this sample |
| `M.managementPointType`, `M.managementPointCategory`, `M.managementPointSubType` | Zone classification | Strings `climateControl`, `primary`, `mainZone` | — | R, metadata | Type used for discovery, category and subtype are unmapped | Use zone metadata to scope hydronic readings | Observed |
| `M.name.value` | User-visible zone label | String withheld | — | R, write flag unrecorded | Yes, name | Keep as inventory metadata | Present, saved name matched privately |
| `M.onOffMode.value` | Reported enable setting | String `on`, allowed `off`, `on` | — | R/W advertised | Yes, power state and capability | Keep distinct from compressor or active heating status | Observed, write untested |
| `M.operationMode.value` | Configured operating mode | String `heating`, sole listed value `heating` | — | R, `settable: false` | Yes, reported mode. Adapter exposed no mode commands | Keep as a mode indicator | Read and metadata observed |
| `M.controlMode.value` | Current control reference | String `leavingWaterTemperature`, listed values also include `externalRoomTemperature` and `roomTemperature` | — | R, `settable: false` | No | Show the current control reference. Treat other listed values as metadata | Read and enum observed |
| `M.setpointMode.value` | Target calculation mode | String `fixed`, listed values `fixed`, `weatherDependent` | — | R, `settable: false` | No | Show alongside the water target to explain how it is determined | Read and enum observed |
| `M.sensoryData.value.leavingWaterTemperature.value` | Measured leaving water temperature | `40`, advertised range −127 to 127, step 1 | `C` | R, `settable: false` | No | Add a dedicated leaving-water temperature metric | Value and range observed |
| `M.sensoryData.value.outdoorTemperature.value` | Measured outdoor temperature | `10`, advertised range −127 to 127, step 1 | `C` | R, `settable: false` | Yes, outdoor temperature | Retain existing mapping | Value and range observed |
| `M.temperatureControl.value.operationModes.heating.setpoints.leavingWaterTemperature.value` | Leaving-water target for heating | `39`, advertised range 28–60, step 1 | `C` | R/W advertised | No. The current mapper looks for a room setpoint | Add a separate water target metric. Keep write support outside this investigation | Value and write metadata observed, write untested |
| `M.temperatureControl.value.operationModes.auto.setpoints.leavingWaterTemperature.value` | Leaving-water target in the returned `auto` branch | `39`, advertised range 28–60, step 1 | `C` | R/W advertised | No | Keep separate from the active heating target. Gate any future use on verified mode support | Branch observed. `auto` was absent from the operation-mode values |
| `M.isInErrorState.value`, `M.errorCode.value` | Error flag and current code field | Boolean `false`; code field is a string, no example recorded | — | R, both `settable: false` | Yes, combined fault when the flag is true | Retain flag-gated fault handling | Fields observed. No fault was active in the snapshot |
| `M.isInWarningState.value` | Warning flag | Boolean `false` | — | R, `settable: false` | No | Add a separate warning indicator | Observed |
| `M.isInEmergencyState.value` | Emergency state flag | Boolean `false` | — | R, `settable: false` | No | Add a distinct emergency indicator | Observed |
| `M.isInInstallerState.value` | Installer state flag | Boolean `false` | — | R, `settable: false` | No | Surface for diagnostics if useful | Observed |
| `M.isHolidayModeActive.value` | Active holiday status | Boolean `false` | — | R, `settable: false` | No | Show separately from configuration | Observed |
| `M.holidayMode.value.enabled`, `.startDate`, `.endDate` | Holiday configuration object children | Boolean `false`; date strings withheld | — | R. Parent `holidayMode` advertises writable | No | Consider read-only status first | Object and field types observed. Child write semantics unverified |

The heat pump reported `leavingWaterTemperature` control and a `fixed` setpoint. Its 39 °C water target has an advertised minimum of 28 °C. That range applies to water temperature. A 10 °C room heating setpoint cannot be established from this unit's response. The `on` setting and `heating` mode describe reported configuration.

## Electrical energy

The read-only `M.consumptionData.value` object contains a heating series under `electrical`. The arrays contain both numbers and nulls. Keep those distinctions during ingestion. A returned zero is an observed bucket value. Null represents a missing bucket for mapping purposes. The capture kept a small safe prefix of each array. It recorded the existence and JSON type of the sibling `unit` field, with its literal value omitted by the sanitizer. The array cadence, window boundaries, ordering and unit value remain verification items.

| Provider field path | Description | Example and data type | Unit | Readable / writable | Currently mapped | Recommended mapping | Confidence |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `M.consumptionData.value.electrical.unit` | Unit shared by the electrical dataset | String, literal not retained | Resolve from the provider field | R via read-only parent | No | Parse and retain the literal on future ingestion | Field and type observed |
| `M.consumptionData.value.electrical.heating.d` | Heating electrical bucket array `d` | 24 number/null elements, starts with numeric zero | Sibling `electrical.unit` | R via read-only parent | No | Map as a windowed series after confirming bucket semantics | Shape verified, interval and units unresolved |
| `M.consumptionData.value.electrical.heating.w` | Heating electrical bucket array `w` | 14 number/null elements, starts with null | Sibling `electrical.unit` | R via read-only parent | No | Keep as its own resolution; preserve missing buckets | Shape verified, interval and units unresolved |
| `M.consumptionData.value.electrical.heating.m` | Heating electrical bucket array `m` | 24 number/null elements, starts with null | Sibling `electrical.unit` | R via read-only parent | No | Keep as its own resolution; avoid adding overlapping windows together | Shape verified, interval and units unresolved |

## Secondary management points

| Provider field path | Description | Example and data type | Unit | Readable / writable | Currently mapped | Recommended mapping | Confidence |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `G.managementPoints[0].managementPointType`, `.managementPointCategory`, `.embeddedId` | Gateway point classification and binding metadata | `gateway`, `secondary`; ID withheld | — | R, metadata | No, secondary point skipped | Keep for diagnostics only | Observed |
| `G.managementPoints[0].isFirmwareUpdateSupported.value` | Firmware upgrade capability indicator | Boolean `true` | — | R, `settable: false` | No | Inventory metadata | Observed |
| `G.managementPoints[0].firmwareVersion`, `.modelInfo`, `.serialNumber`, `.ipAddress`, `.macAddress` | Gateway inventory and network metadata | Characteristic objects, values withheld | — | R, write flags unrecorded | No, secondary point skipped | Restrict to private inventory if needed | Objects observed |
| `G.managementPoints[2].managementPointType`, `.managementPointCategory`, `.embeddedId` | Indoor hydronic unit classification and metadata | `indoorUnitHydro`, `secondary`; ID withheld | — | R, metadata | No, secondary point skipped | Inventory metadata | Observed |
| `G.managementPoints[2].modelInfo`, `.softwareVersion.value` | Indoor unit inventory | Model object and version string, values withheld | — | R, software version read-only | No | Inventory metadata | Observed |
| `G.managementPoints[3].managementPointType`, `.managementPointCategory`, `.embeddedId` | Outdoor unit classification and metadata | `outdoorUnit`, `secondary`; ID withheld | — | R, metadata | No, secondary point skipped | Inventory metadata | Observed |
| `G.managementPoints[3].softwareVersion.value` | Outdoor unit software version | String withheld | — | R, `settable: false` | No | Inventory metadata | Observed |
| `G.managementPoints[4].managementPointType`, `.managementPointCategory`, `.embeddedId` | User interface classification and metadata | `userInterface`, `secondary`; ID withheld | — | R, metadata | No, secondary point skipped | Inventory metadata | Observed |
| `G.managementPoints[4].modelInfo`, `.softwareVersion.value` | Interface inventory | Model object and version string, values withheld | — | R, software version read-only | No | Inventory metadata | Observed |

## Unavailable in this response

| Provider field path | Description | Example | Unit | Readable / writable | Currently mapped | Recommended mapping | Confidence |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Unreturned | Return, inlet or other loop water temperatures | — | — | Unverified | No | Leave unavailable | No corresponding field in this response |
| Unreturned | DHW point, tank temperature, target, or hot water operation | — | — | Unverified | No | Leave unavailable | No DHW management point or measurements in this response |
| Unreturned | Actual heating demand, compressor operation or speed, defrost, pump state, flow rate, hydraulic pressure | — | — | Unverified | No | Leave unavailable. A configuration or reachability flag cannot establish these states | No corresponding fields in this response |
| Unreturned | Instantaneous electrical power, delivered heat, COP, DHW/cooling consumption, lifetime counters | — | — | Unverified | No | Support separate measurements when their provider paths are verified | Only the heating electrical bucket arrays were returned |
| Unreturned | Indoor room temperature and room setpoint, humidity, fan speed, frost-protection mode | — | — | Unverified | Generic adapter handles some fields for other units | Leave unavailable for this device | The live adapter returned null for room temperature, room setpoint, humidity and fan |
| Unreturned | Warning code, fault history, defrost history | — | — | Unverified | No | Retain the current error code and separately map the verified warning flag | No corresponding history or warning code in this response |

## Highest-value additions from the initial inspection

1. Map **leaving-water temperature** and the **active heating water target** as separate fields. The live examples were 40 °C measured and 39 °C targeted.
2. Surface **control mode**, **setpoint mode**, **warning** and **emergency** indicators for diagnosing how this unit is operating.
3. Validate the gateway timestamp and the units and windows of the **heating electrical energy** arrays before enabling source freshness or history based on those fields.

## Milestone 3D verification

A read-only discovery on 2026-11-22 matched the same Altherma using its existing gateway and climate management-point IDs. It reported 40 °C leaving water, 39 °C target leaving water, and 10 °C outdoors. These are observations from the check. The tables above retain the mapping status at the time of the initial inventory.

The subsequent normal worker cycle stored 40 °C leaving water, a 39 °C target, and 11 °C outdoors. The reported mode was heating and the power setting was on. The MySQL history query returned one sample per water-temperature series for the existing property and none across a different property ID. The chart uses two samples as its display threshold.

| Provider field path | Milestone 3D handling | Verification |
| --- | --- | --- |
| `M.sensoryData.value.leavingWaterTemperature` | Read as leaving-water temperature and store the numeric value in °C | Confirmed live |
| `M.temperatureControl.value.operationModes.heating.setpoints.leavingWaterTemperature` | Read the heating target in °C and retain range, increment, and advertised writability as metadata | Confirmed live. Application command paths block this equipment |
| `M.controlMode.value`, `M.setpointMode.value` | Display and store the provider strings | Observed `leavingWaterTemperature` and `fixed` |
| `M.isInWarningState.value`, `M.isInEmergencyState.value` | Display and store explicit Boolean values | Both observed as `false` |
| `G.timestamp` | Retain as gateway metadata. Track telemetry receipt separately when `lastUpdateReceived` is absent | ISO timestamp observed. Device observation semantics unverified |
| `M.consumptionData.value.electrical.unit` | Recognize the verified dataset unit in this inventory | Returned `kWh` |
| `M.consumptionData.value.electrical.heating.{d,w,m}` | Await bucket boundaries, cadence, ordering, and accumulation semantics before ingesting | 24, 14, and 24 numeric-or-null positions. The response supplied no explicit interval boundaries |

Return-water temperature, DHW, compressor and defrost status, flow rate, and delivered heat remain unavailable from the observed response. See the [ONECTA integration guide](daikin-onecta.md#milestone-3d) for monitoring and AC write controls.
