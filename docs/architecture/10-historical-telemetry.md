# Historical telemetry

Milestone 7A adds provider-neutral history on top of the existing raw store. Every hourly row, daily summary and history response belongs to one physical `telemetry_series`. Raw `telemetry_samples` stay the source of truth and are never rewritten or deleted.

## Raw observations

- `TelemetryWriter.PersistAsync` remains the single ingestion path for Growatt, Daikin, eWeLink, Tuya and the simulator. Ingestion is unchanged.
- Raw rows are keyed `(series_id, observed_at_utc)`. A repeated observation instant keeps its first stored value. `latest_readings` only advances on a strictly newer observation, so late or out-of-order rows enter history without regressing current state. There is no correction mechanism. A corrected value would need a new explicit, audited path rather than last-write-wins.
- `observed_at_utc` is the provider time when one exists. `received_at_utc` is the server receipt time. `timestamp_source = 'serverReceived'` marks observations whose only time is receipt, such as timestamp-less eWeLink frames. The raw history API reports this as `timestampSource`.
- Adapters reject non-finite values before persistence. Aggregation skips any stored value that is unusable for its metric and marks the bucket `invalidValue`. Missing readings are never converted to zero. Tuya re-persists last-known readings with their original timestamps, so the primary key makes them idempotent.

## Hourly rollups

Migration `017_telemetry_rollups.sql` adds two tables.

| Table | Purpose |
| --- | --- |
| `telemetry_rollups_hourly` | Primary key `(series_id, bucket_start_utc)`. UTC hours with observation count, covered seconds, min/max, time-weighted average, value sum for the arithmetic mean, first and last observation with its value, relay ON seconds and transitions, measured Wh, estimated positive and negative Wh, quality flags and calculation time. Columns that do not apply to a metric stay `NULL`. |
| `telemetry_rollup_state` | Per series, the contiguous range `[rolled_from_utc, rolled_to_utc)` whose hours are materialized. The API reports time outside it as `notAggregated` instead of returning empty values. |

The metric kind comes from the `Metric` capability, not the device model.

| Kind | Metrics | Hourly values |
| --- | --- | --- |
| Gauge | Temperature, humidity, power, voltage, current, frequency, SOC and setpoints | Min/max, time-weighted average, arithmetic mean. W metrics also integrate estimated Wh. |
| Boolean | `SwitchOn`, `GridAvailable`, Daikin warning and emergency flags | Covered seconds, ON seconds, observed transitions |
| Daily-reset counter | Growatt `PvEnergyToday`, `OutputEnergyToday`, `BatteryDischargedEnergyToday` | Measured Wh |
| Cumulative counter | Simulator `Energy` | Measured Wh |
| Status | Operating and setpoint modes, warning codes | Raw history only |

### Bounded hold and coverage

Each observation represents its value until the next observation, for at most the series' maximum hold, and never beyond the calculation time. Time outside a hold stays uncovered. Covered seconds are counted separately from the observation count. The hold reuses each provider's existing freshness window.

| Source | Maximum hold (defaults) |
| --- | --- |
| Growatt | `Growatt:FreshnessSeconds` (900 s) |
| Daikin | `Daikin:FreshMinutes` (35 min) |
| eWeLink power, voltage, current | WebSocket freshness (2 min) |
| eWeLink relay and climate | `Ewelink:PollMinutes` + 2 (7 min) |
| Tuya and `tuya-sensor-report` | 2 × `Tuya:PollMinutes` + 5 (65 min) |
| Simulator | `Simulator:FreshnessSeconds` (45 s) |

Coverage is covered seconds divided by the elapsed period. A bucket is `incomplete` below 80 %, `noData` without observations or coverage, `notAggregated` when part of it lies outside the materialized range, and otherwise `complete`. Buckets ending within the 72-hour recompute window are marked `provisional`. A period that has not yet ended is flagged `partialPeriod`.

### Averages

`average` is time-weighted over covered seconds. `mean` is the arithmetic mean of the observations recorded inside the bucket. A value carried into a bucket from an earlier observation contributes to `average`, `minimum`, `maximum` and coverage, but not to `sampleCount` or `mean`. Daily averages weight hourly averages by covered seconds.

## Energy

Measured and estimated energy are separate fields and are never added together.

- **Estimated from power.** W gauges integrate the held value over covered time. Positive and negative Wh are kept separately, so `BatteryPower` reports charge (positive) and discharge (negative) energy instead of a net value. A gap longer than the hold contributes nothing. Status is `estimated`, or `incomplete` below 80 % coverage.
- **Measured counters.** A delta is calculated only between observations of the same series.
  - Daily-reset counters restart from zero at local midnight in the counter's zone. For Growatt this is `Growatt:TimeZone`, the same zone the live daily-counter freshness rule uses. The property zone is the fallback. Energy between the last observation of a day and midnight is unobserved and stays uncovered.
  - A decrease within one day, or a step implying more than 100 kW, is rejected and flagged `counterDiscontinuity`.
  - A cumulative counter that decreases is treated as a reset or rollover of unknown modulus, flagged `counterReset`, and starts a new baseline. Cumulative steps longer than 24 hours are not differenced.
  - A step within the hold is spread across the hours it spans. A longer step keeps its measured total in the hour of its closing observation and is flagged `counterGap`.
  - Status is `measured` only for complete buckets without counter flags. Otherwise it is `incomplete`.
- **Growatt.** Per-inverter counters stay per inverter. The existing mapper writes installation-level `PvPower` (only when both inverters report at the same instant) and the master's whole-site `HouseholdPower`. History never sums device series. Shared-battery and site totals come only from their own series.
- **eWeLink and Tuya.** Both report W, not kWh, so their energy is always estimated. eWeLink electrical frames arrive only during an activation window: from an operator's live view or, since Milestone 7D, from optional [background collection](13-ewelink-electrical-collection.md). The 2-minute hold is unchanged, so their estimates normally show partial coverage.

## Calendar days

Hourly rows are UTC. A daily bucket is the local calendar day in `properties.time_zone`, including 23- and 25-hour DST days and zones with non-whole-hour offsets such as Asia/Kolkata and Asia/Kathmandu. `HistoryPlanner` splits each day into whole materialized hours and partial-hour edges. The same calculator computes the edges from raw samples, so a daily summary equals a direct raw calculation over the exact local day. Unit tests verify this. Hours after the materialized range are calculated from raw for at most 48 hours. Earlier time is `notAggregated`.

Milestone 7B uses the same mechanism to split a logical device's history at any replacement instant. See [logical devices](11-logical-devices.md#continuous-history). Counter deltas never cross series.

## Aggregation worker

`TelemetryRollupWorker` starts with the API and is enabled by default under `TelemetryHistory`. Each run, every 15 minutes:

- waits until migration 017 is recorded;
- acquires the named lock `homeautomation:telemetry-rollups` scoped to the current schema, and skips the run when the backfill or another worker on the same schema holds it;
- recomputes the most recent 72 complete hours of every series;
- catches up from `rolled_to_utc` in seven-day steps after downtime.

Each series is recomputed in its own transaction. Rows are written only when a value changed, and the state advances in the same transaction. Raw samples are read without locks, so ingestion is not blocked. Late data older than 72 hours needs an explicit backfill rerun.

## Backfill

The backfill runs only when started explicitly from the command line, and is a dry run unless `--execute` is given. It processes each series newest first in seven-day chunks, each committed separately. An interrupted run keeps completed chunks. Rerunning the same command continues from the remaining range, and the default end is the current `rolled_from_utc`. Rows are upserts, so repeated runs write nothing new. Hours without raw observations stay empty. History that was never collected cannot be reconstructed. See the [README](../../README.md#historical-telemetry) for commands.

## History API

All routes are under `/api/v1/properties/{propertyId}` and inherit the property membership filter.

| Route | Description |
| --- | --- |
| `GET history/series?deviceId=&metric=` | Property series with unit, source, subject, aggregation kind, average and energy basis, maximum hold, aggregated range, and whether the series is the preferred source. |
| `GET history?…&resolution=raw` | Raw observations in `[from, to)` up to 31 days, `limit` 1–5000 (default 1000), paged with `after`. |
| `GET history?…&resolution=hour` | UTC hours in `[from, to)` up to 31 days, with one bucket per hour so gaps are explicit. |
| `GET history?…&resolution=day&fromDate=&toDate=` | Local calendar days, up to 366. |

Select exactly one of `seriesId`, `endpointId`, `deviceId` or `installationId`, and add `metric` unless you select a series. When one endpoint or installation reports a metric through several streams, `tuya-sensor-report` is preferred, then the oldest series. The response lists the other streams in `alternativeSeriesIds`. Streams are never merged. A device whose metric exists on several endpoints returns 400 with candidate series. Energy values are Wh, and clients convert to kWh for presentation. The existing `/telemetry`, Tuya sensor-history and Daikin water-history endpoints are unchanged.

## Storage

Raw rows take about 100 bytes on disk including the primary key. Hourly rows take about 170 bytes. No automatic deletion exists in 7A.

| Example | Raw per year | Hourly per year |
| --- | --- | --- |
| One series every 5 minutes (Growatt) | 105,120 rows, about 10 MB | 8,760 rows, about 1.5 MB |
| One series every 30 minutes (Tuya) | 17,520 rows, about 1.8 MB | about 1.5 MB |
| 40 Growatt series and 20 Tuya/eWeLink series | about 440 MB | about 90 MB |
| eWeLink live electrical frames | about 3,600 rows per metric per viewed hour; background windows add a few rows per meter per window | at most 8,760 rows per metric |

Measure actual growth before choosing retention.

## Limitations and deferred decisions

- eWeLink temperature and electrical values reach history only from WebSocket frames (see [continuous eWeLink electrical collection](13-ewelink-electrical-collection.md)). HTTP synchronization persists relay state only. Tuya relay state that has not changed keeps its original status time, so long ON periods appear as `incomplete` coverage rather than inferred ON time.
- The hold policy is code-defined from provider options. Changing an option changes later rollups. Rerun the backfill to apply it to older hours.
- Late observations older than the 72-hour window are not picked up automatically.
- No property-wide energy total is calculated. Installation totals remain the explicit installation-level series.
- Retention, a persisted daily table and partitioning remain future decisions after storage measurement.
