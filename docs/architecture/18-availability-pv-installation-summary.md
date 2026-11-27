# Device availability, PV generation and the installation summary

The live views and alert evaluation use four derived rules. All of them are in [`DerivedState.cs`](../../src/backend/HomeAutomation.Domain/DerivedState.cs). They are computed when the views are read: no migration, nothing stored, and raw telemetry, history and energy unchanged. They were introduced by [prompt 0068](../prompts/0068-growatt-availability-pv-search-solar-summary.md).

## Device availability

### Separate concepts

| Concept | Where it lives |
| --- | --- |
| Provider-reported status | `device_state.connectivity`, with the provider's source time `observed_at_utc`. Shown as `reportedConnectivity` and `reportedAt`. |
| Successful provider communication | The provider poller's state, for example Growatt `lastSuccess`, `degraded` and `paused`. |
| Latest source telemetry time | The newest valid `latest_readings.observed_at_utc` of the device (`latestReadingAt`). |
| Ingestion time | `received_at_utc` on samples. It is not availability evidence. |
| Application availability | `availability` (Online, Stale, Offline, Unknown), with `availabilityBasis`. This is the primary status. |
| Telemetry freshness | Each reading's `quality` (fresh, stale, missing), from the provider's freshness window (`TelemetryHoldPolicy.Hold`). |

### Evaluation

`DeviceAvailability.Evaluate(reported, reportedAt, latestReadingAt, now, window)` runs in this order:

1. **Online (`recentReading`):** a valid reading within `window` of now, unless the provider reported Offline after that reading, recently.
2. **Offline (`providerOffline`):** a provider Offline report within `window` that no newer reading contradicts. This is the only route to Offline: a confirmed disconnection.
3. **Online (`providerOnline`):** a provider Online report within `window`, for example a relay whose readings have not changed.
4. **Stale (`noRecentEvidence`):** some evidence exists, but none is recent. An expired window alone never means Offline.
5. **Unknown (`noEvidence`):** no reading and no Online or Offline report. An Unknown report by itself is not evidence.

### Rules for the evidence

- **Source times only.** A poll that returns an old reading again does not refresh it, because the reading keeps its source time.
- **Ordering.** `device_state` and `latest_readings` only move forward, so duplicate and out-of-order telemetry cannot go back in time.
- **Clock skew.** A timestamp up to 2 minutes in the future counts as now. Anything later is ignored.
- **Restarts.** Everything is recomputed from stored rows on every read, so a restart changes nothing.
- **Same-record flags.** Growatt sends its `lost` flag in the same record as the readings. An Offline flag that arrives with fresh readings therefore yields Online, and the flag stays visible as `reportedConnectivity`. Live checks had found `lost` set on both inverters while data was current ([Growatt guide](../integrations/growatt.md)).

### The window

`TelemetryHoldPolicy.AvailabilityWindow(provider)` is used by both the monitoring reader and alert evaluation.

| Provider | Window | Default |
| --- | --- | --- |
| Growatt | `PollSeconds × 1.5` | 300 s gives 450 s, so readings 4 and 6 minutes old are Online and 8 minutes old are Stale |
| Daikin, eWeLink, Tuya, simulator | Their existing freshness windows: Daikin `FreshMinutes`, eWeLink `PollMinutes + 2`, Tuya `2 × PollMinutes + 5`, simulator `FreshnessSeconds` | Unchanged |

The other providers keep their windows because their own pages and eWeLink relay eligibility use the same values. Changing those windows is a separate decision.

Reading freshness is separate and unchanged. For Growatt it is `FreshnessSeconds`, default 900 s and at least twice the poll interval, and history holds use it. A Growatt inverter can therefore be Stale after 7.5 minutes while its readings are still marked fresh until 15 minutes. The badge's tooltip explains which applies.

### Where it is used

- **Shared state:** `DeviceDetails.availability` from `/state`, `/devices/{id}` and `/growatt`.
- **Pages:** the Devices page badge, device pages, and the Growatt cards and Master/Slave notice (`reportedConnectivity`).
- **Legacy field:** `connectivity` now carries availability in its three-value form, with Stale reading as Unknown.
- **Alert rules:** the Connectivity signal and its catalog entry. Its text is Online, Stale, Offline or Unknown, and its observation time is the evidence's source time. A condition's maximum reading age still applies, so Stale evidence older than that age makes the condition Unknown.
- **Stored rules:** existing rules that compare connectivity with Online, Offline or Unknown keep working.
- **Provider events:** the provider's own transition is still recorded as a `DEVICE_OFFLINE` device event. Its signal label now reads "reported offline (provider)".
- **Not changed:** the Daikin, eWeLink and Tuya provider pages keep their own provider status, and control eligibility is unchanged.

## PV display and generation

### Display

`PvGeneration.Display` and the frontend `displayNumber` apply one rule:

- **0–10 W inclusive:** shown as 0 W. This is noise or residual power, for example 1 W at night.
- **Above 10 W:** shown as reported.
- **Negative or non-finite:** Invalid, never production. The Growatt mapping already drops negative inputs.

The rule applies in the Growatt cards, the Devices page, the dashboard and the installation summary. The stored value, history, rollups and energy keep the raw reading. A card showing 0 W for a nonzero reading names the raw value in its tooltip.

### Generation state

`PvGeneration.Evaluate`, one result per inverter PV input:

- **Hysteresis.** Generating starts above 20 W and ends at 10 W or below. Between the two, the previous state holds. It is found by replaying the input's samples from the last 3 hours, at most 12, oldest first. Without a prior state, the result is Not generating.
- **Voltage veto.** A fresh PV voltage of exactly 0 V means no PV input, so the result is Not generating whatever power is reported. No other voltage threshold is assumed, because Growatt does not publish one that holds across inverter models. A stale 0 V does not veto fresh power.
- **Unknown.** Stale, missing or invalid power gives Unknown, shown as stale or unavailable, never as generating.

The Growatt state returns this per PV input as `pvInputs`, so the state is the same for every viewer and after a reload. The sample read is bounded by the `(series_id, observed_at_utc)` primary key, and only the Growatt view runs it.

## Installation summary

The Live Growatt **Solar installation** section shows four values (`PropertyState.solar`). Each one is computed from the inverters' latest readings, unless the provider sends a current whole-installation reading for that value.

### Why it showed Unavailable

The summary looked up installation-level series, and the V4 adapter never produces any. Each of the four had its own gap:

- **PV production:** an installation `PvPower` series existed only with `Growatt:SumIndependentPv=true` (default false) and exactly equal timestamps.
- **Household consumption:** this needed `Growatt:MasterReportsWholeSiteLoad=true` and a master `HouseholdPower` reading. The V4 mapping never produces that reading.
- **Grid availability:** nothing produces `GridAvailable`. The mapping intentionally infers no grid state.
- **Grid import:** the summary asked for `GridImportPower`. V4 reports only each inverter's `AcInputPower` (labelled **Import from grid** on the cards).

### Values

| Slot | Label | Formula | Available when |
| --- | --- | --- | --- |
| PV | **PV production** | `Σ Display(pvPower)` over the inverters. Each inverter measures its own PV input, and the 10 W rule applies once, per inverter, as on the cards. | Every inverter has a fresh reading, observed within one poll interval of each other |
| Consumption | **Inverter output** | `Σ activePower` (inverter AC output). Loads supplied by the inverters; loads wired outside them are not included. | As above |
| Grid | **AC input supply** | From each inverter's AC input voltage, with the grid outage alert template's thresholds (`GridSupply`): absent only when every inverter reads 10 V or less (the template's All aggregation), present when one reads above 180 V. | Every inverter agrees |
| Import | **AC input power** | `Σ acInputPower`. 0 W is a valid value. | As above |

### States

- **Partial:** some inverters have no current value, or the readings are further apart than one poll interval. The value names who is missing, for example "Only Growatt master reported a current value; Growatt slave did not."
- **AC input disagreement:** one inverter reads above 180 V and another reads 0 V. The supply shows present, partial, with the reason.
- **Stale:** only old readings exist. The value is shown with "(stale)", or Unknown when not every inverter has one.
- **Unavailable:** nothing has been reported. The reason is shown and no number is invented.

### Provider totals

When the provider sends a current installation reading (`PvPower`, `HouseholdPower`, `GridAvailable` or `GridImportPower`), that reading is used. It appears with the installation label: **PV production**, **Household consumption**, **Grid availability** or **Grid import**.

### Limitations

- **Transfer switch.** The AC input is measured after the transfer switch, so supply present means AC supply at the inverters, not necessarily the utility grid.
- **Inverter output.** It includes power the inverters deliver while charging from the grid only as far as that power reaches the loads. Battery charging, grid charging and self-consumption are not added to it.
- **No new rule.** No second grid outage algorithm exists. The thresholds are the constants the grid outage templates use.

## Device search

The Devices page filters what it already loaded and never calls a provider.

- **Filtering.** The search is case-insensitive and trimmed. Every word must appear somewhere in the device's fields. Filtering is deferred while typing, and live updates keep the active query.
- **Provider devices.** Fields: custom and provider name, manufacturer, model, provider name and code, kind, room, endpoint names and `#id`.
- **Logical devices.** Fields: name, description, kind, room, and the name, provider, manufacturer, model and channel of every current or previous device.
- **Other sections.** **Unassigned and new hardware**, and **Devices in your other properties** (properties the user already manages).
- **Empty results.** Sections with nothing matching say so or disappear. When nothing matches anywhere, the page shows *No matching devices for "…"*.
