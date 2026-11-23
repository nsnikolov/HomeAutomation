# Fix eWeLink inventory pagination for the missing seventh device

Date: 2026-11-23

## Original prompt

Investigate and fix eWeLink inventory pagination for the missing seventh device.



Live sanitized diagnostics show:

- 1 home discovered.
- CoolKit reports total=7 things.
- The first page returns 6 valid owned devices.
- 0 excluded, 0 unmapped, 0 duplicates.
- Current implementation stops because the page is shorter than PageSize, despite total=7.
- "Thermostat Bidon" appears in the official eWeLink app but is absent from our inventory.

Tasks:

1. Review the existing eWeLink pagination implementation and official CoolKit get-thing-list pagination semantics, especially beginIndex, item indexes and total.
2. Determine whether a short page can legitimately precede further results.
3. If supported, continue pagination safely when returned unique indexes have not reached the reported total, even if the page is short.
4. Preserve request budgets, cursor-progress checks, cycle detection, rate-limit handling and existing backoff protections.
5. Handle inaccessible or filtered devices correctly. Never invent missing devices or loop indefinitely.
6. Add tests for total=7 with 6 items on the first page, including both a recoverable next page and an API that never returns the missing device.
7. Keep sanitized inventory diagnostics showing why discovery stopped.
8. Run builds and tests, archive the prompt and commit locally. Do not push.

Keep changes focused on eWeLink discovery. No physical device commands.

## Execution summary

The eWeLink adapter now follows an advancing cursor when a short page leaves a known reported total unmet. Tests covered returning the seventh device, repeated and empty follow-up pages, and enforcement of the existing request budget. The sanitized diagnostics report the stop reason and the number of unique indexes observed. CoolKit documents that brand restrictions can leave a total unmet, so Thermostat Bidon's live availability remains unverified.

Both builds passed. Backend tests recorded 125 passes and five opt-in MySQL skips. All 27 frontend tests passed. Verification used fixtures and made no live provider requests.

See [pagination](../../src/backend/HomeAutomation.Infrastructure/Ewelink/EwelinkClient.cs), [regressions](../../tests/HomeAutomation.UnitTests/EwelinkTests.cs), and the [eWeLink guide](../integrations/ewelink-cloud.md#discovery-and-capability-based-measurements).
