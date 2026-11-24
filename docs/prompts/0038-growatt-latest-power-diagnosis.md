# Diagnose Missing Growatt AC Input and Battery Power

Date: 2026-11-23

## Original prompt

# Diagnose Missing Growatt AC Input and Battery Power

After commit c3c3141, both SPF 5000 ES inverter cards still show:

- AC input power: Unavailable
- Battery charge/discharge: Unavailable

The previous implementation confirmed:

- pAcInPut exists in V4 historical responses.
- pBat exists in V4 historical responses.
- It is not yet verified whether these fields exist in the scheduled latest-response endpoint.

The dashboard also reports intermittent failed Growatt polls.



Tasks:

1. Trace the exact scheduled polling endpoint and response model.
2. Enable or inspect Growatt:TelemetryDiagnostics.
3. Determine whether pAcInPut and pBat are present, null, zero, or absent in the actual latest responses.
4. If present, fix parsing, mapping, persistence or frontend projection as needed.
5. If absent, investigate whether the existing V4 history request can safely provide sufficiently recent measurements without exceeding current API quotas.
6. Compare source timestamps and preserve stale-data indicators. Never present historical readings as live.
7. Investigate the failed poll separately, including rate limiting and backoff.
8. Preserve master/slave separation and battery direction conventions.
9. Do not introduce additional recurring API calls without documenting their quota impact.
10. Add regression tests and run both builds.

Do not send inverter commands.



Archive the prompt and commit locally without pushing.



Report the exact cause of Unavailable and the proposed solution if the existing endpoint cannot provide these fields.

## Execution summary

Observed the existing scheduled V4 `queryLastData` poll with telemetry diagnostics already enabled. The first post-deployment attempt was throttled and delayed for ten minutes. The previous stored poll was only 92 seconds before the restart. The original warning does not establish the HTTP status or provider code. The first recovered poll supplied numeric `pAcInPut` and `pBat` for Master and Slave and persisted both at 22:16:25 EET on November 23. Before that poll the new power series had no readings. The live MySQL audit and dashboard reader confirmed 0 W input and discharging battery readings on both roles, with stale quality after the source observations aged out.

An independent history read returned the same source timestamps as the successful latest poll, so the existing route suffices. Added diagnostics for field state, observation time, request operation and sanitized failure codes. Poll cadence, retry timing and record scope were preserved. No recurring calls or inverter commands were added.

Both builds passed. Backend tests passed 159 with seven opt-in database tests skipped. All 34 frontend tests passed in the serial rerun after an existing Tuya render test timed out during a parallel run.

- [Diagnosis and request budget](../integrations/growatt.md#diagnosing-missing-ac-input-and-battery-readings)
- [Scheduled polling diagnostics](../../src/backend/HomeAutomation.Api/GrowattWorker.cs)
