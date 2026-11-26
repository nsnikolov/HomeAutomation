# History charts

Milestone 7C adds a **History** page (`/properties/{id}/history`) that charts the logical-device history of Milestones 7A and 7B. It is read-only and needs no migration beyond 017 and 018.

## Page

- The main navigation has a History entry. Each logical device on the Devices page links to its history (`?device={id}`).
- Filters live in the URL: `device`, `channel`, `metric`, `range`, `res`, custom `from` and `to` (property-local), and `compare` (`{id}:{channel}` list). Links can therefore be shared and the browser's back button works.
- The measurement list shows the metrics recorded for a logical device across all its assignments (`metrics` in the logical device API). Metrics the current device no longer reports stay selectable and are labelled. Status codes are raw-only and are not charted.
- The page is lazy-loaded, so its code is only downloaded when opened.

## Ranges and resolutions

The window is captured when a range is chosen. **Refresh** moves it to the current time. Query keys therefore stay stable, and TanStack Query cancels superseded requests.

| Range | Default | Other choices | Request |
| --- | --- | --- | --- |
| Last 24 hours | Hourly | Individual readings | `from`/`to` instants |
| Last 7 days | Hourly | Daily | `from`/`to` instants |
| Last 30 days | Daily | Hourly | property-local `fromDate`/`toDate` |
| Last 12 months | Monthly | Daily | daily buckets from the first of the month 11 months ago |
| Custom | Automatic by span | bounded by API limits (31 days hourly, 366 days daily) | property-local wall-clock inputs converted to UTC |

Individual readings are limited to 5,000 points per request, and the page says so when more exist. Raw views also request hourly buckets for their summary.

## Correctness rules

- **Monthly values** are combined in the browser from property-local daily buckets, grouped by each bucket's local `date`. They use the backend's rules: averages weighted by covered seconds, extremes over all days, energy totals summed, and a month marked measured or estimated only when every day is. No monthly table or endpoint was added.
- **Summaries** use the same weighted combination over the range's buckets, never an average of averages. Coverage is covered seconds over elapsed seconds. A range that is partly unaggregated says so.
- **Gaps** are explicit:
  - buckets with no reading, not yet aggregated, or not reported by the assigned device produce null values, so lines break;
  - raw lines break where readings are further apart than the source's hold (`maximumHoldSeconds` on each segment) and at every device change;
  - zero readings remain zero.
- **Quality** appears as a strip with a separate pattern for each state (complete, incomplete below 80 %, no reading, not aggregated, not reported), a legend with counts, and explanatory tooltips.
- **Energy:**
  - counter metrics show measured energy as solid bars;
  - power metrics show energy estimated from power as hatched bars in a separate, labelled chart;
  - partial intervals are translucent with a dashed outline;
  - measured and estimated values are never stacked or added;
  - signed power shows the negative direction separately.

  Counter deltas across replacements come only from the backend's per-assignment calculation. Growatt series are shown per logical device only; no property-wide totals are created.
- **Relays** show ON, OFF and uncovered hours per bucket, plus ON and covered duration.
- **Replacements** come from the backend's assignment segments, never from provider names. Each boundary inside the window is listed with its exact time and the old and new device names. On bucket charts the marker is drawn on the bucket that contains the replacement instant; on raw charts it is drawn at the exact time. A note explains when the assigned device does not report the selected metric.
- **Comparison** adds up to three logical devices that record the same metric. Each series keeps its own buckets, gaps and coverage on a shared time axis. Series are distinguished by both colour and line pattern, and never summed. Comparison is offered for gauge and energy metrics at bucket resolutions.

## Time zones

All labels use the property time zone. Instants in tooltips and replacement markers include the UTC offset, so a repeated local hour during a DST change is unambiguous. Daily labels name 23- and 25-hour days. Custom inputs resolve a repeated local time to its first occurrence, and move a skipped local time forward by the length of the gap.

## Availability

A 503 from the logical device or history API shows "History is not available yet" (migrations 017 and 018). This is kept separate from "No readings were recorded in this range", "History for this range has not been aggregated yet" and a generic load failure. Without logical devices, the page points to the documented initialization command. Existing pages are unchanged.

## Accessibility

Every chart has an accessible name and a visually hidden table with each period's values, gaps and quality. Filters are native, labelled form controls. Estimated energy, comparison series and quality states have non-colour cues. The layout stacks on narrow screens.

## Backend additions

These additions are backwards-compatible and need no migration:

- `metrics` on each logical device: channel, metric, unit, aggregation, and whether the current device reports it.
- `maximumHoldSeconds` on each logical history segment.

## Deferred

- Continuous eWeLink electrical collection: eWeLink power history shows its real, often low, coverage.
- Brush or zoom interactions beyond range selection.
- Export, and comparisons across different metrics.
- Physical-series history without a logical device.
