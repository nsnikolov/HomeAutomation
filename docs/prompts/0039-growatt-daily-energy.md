# HomeAutomation — Growatt Daily Energy Statistics (MVP)

Date: 2026-11-24

## Original prompt

# HomeAutomation — Growatt Daily Energy Statistics (MVP)

Implement a minimal daily energy statistics feature for the existing Growatt SPF 5000 ES integration.



Context:

- Two parallel SPF 5000 ES inverters (Master and Slave).
- Shared LiFePO4 battery.
- Existing .NET 10 backend, MySQL, React dashboard.
- Current Growatt polling works through queryLastData every five minutes.
- AC input power and battery power are now available.
- Latest Growatt changes are committed as 3cfe64d.

Requirements:

1. Inspect the existing Growatt response mapping and identify daily energy counters, such as:
   - PV generation today (kWh)
   - Load consumption today (kWh)
   - Battery charged today (kWh)
   - Battery discharged today (kWh)
   - AC input energy today (kWh)
2. Use only counters whose actual source, units and semantics can be verified for SPF 5000 ES. Preserve valid zero values. Never derive daily energy by simply summing instantaneous power samples.
3. Reuse existing scheduled responses and stored data. Do not introduce additional recurring Growatt API requests or alter polling, quotas or backoff.
4. If usable daily counters are available, persist them by property, device and local date (Europe/Sofia). Handle counter updates throughout the day, day rollover and stale readings. Avoid duplicate or double-counted data between parallel inverters.
5. Add a compact "Energy today" section to the Growatt page, displaying only verified metrics. Show per-inverter values when site-wide totals cannot safely be established.
6. If the existing response does not contain usable daily energy counters, do not implement speculative calculations or new polling endpoints. Document the missing fields and stop.
7. Add focused tests for counter mapping, units, valid zeros, day rollover and parallel-inverter handling. Run relevant builds and tests.
8. also please add to change label "AC input power" to "Import from grid"

Constraints:

- Read-only Growatt integration.
- No inverter commands.
- No new charts, alerts or unrelated refactoring.
- Minimize exploration and token usage.
- Archive the prompt and commit locally without pushing.

Report which daily energy metrics are confirmed and which remain unavailable.

## Execution summary

Added a per-inverter **Energy today** section for PV generation, inverter output energy and battery discharge. Renamed the instantaneous AC input reading to **Import from grid** and clarified the measurement location. The three counters use the existing five-minute poll and telemetry tables, with provider timestamps evaluated against the Europe/Sofia calendar day. No migration, additional recurring API requests, or inverter commands were needed. Total battery charge energy and total AC input energy lack verified counters for this installation.

A normal poll after restarting the local Development backend returned all three counters on both inverters. The monitoring reader returned six fresh kWh values from local MySQL. Both builds passed, along with 161 backend tests and 34 frontend tests. Seven opt-in database tests were skipped.

- [Growatt daily energy documentation](../integrations/growatt.md#daily-energy-on-the-spf-5000-es)
- [Growatt cloud mapping](../../src/backend/HomeAutomation.Infrastructure/Growatt/GrowattCloudSource.cs)
- [Growatt monitoring view](../../src/frontend/home-automation-web/src/features/GrowattPanel.tsx)
