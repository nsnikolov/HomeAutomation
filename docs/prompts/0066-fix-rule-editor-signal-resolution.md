# HomeAutomation — Fix Rule Editor Signal Resolution and Duplicate Signal Labels

Date: 2026-11-27

## Original prompt

# HomeAutomation — Fix Rule Editor Signal Resolution and Duplicate Signal Labels

## Context

An interactive smoke test of HomeAutomation in the development environment identified three issues in the new Event Rules and Notifications subsystem.

The system was implemented in commit `a2bf0b4`.

Do not implement unrelated functionality.

## Issue 1 — Valid signals appear unavailable (P1)

Reproduction:

1. Navigate to Automations → Alert rules.
2. Select New rule.
3. Add a condition.
4. Choose Growatt shared battery → Main → Battery SOC.
5. The Signal field changes to "The selected device is no longer available."
6. The numeric threshold input disappears.

However, "Preview with latest readings" successfully reads Battery SOC = 38%, with Fresh telemetry.

The same issue occurs with:

- Growatt Master → AC input voltage (244.5 V).
- Battery low template.

### Investigation

Trace the complete signal resolution flow:

- Signal catalog backend API.
- Provider signal descriptors.
- Device and installation identities.
- Capability and endpoint identifiers.
- Frontend signal selection.
- Condition serialization.
- Template initialization.
- Existing rule loading.
- Preview evaluation.

Identify the actual root cause.

Check whether the frontend incorrectly compares signal identifiers, loses selected values, or mixes different identifier formats.

### Requirements

- Every selectable supported signal must remain selected after condition changes.
- Numeric signals must display the appropriate comparison operators and threshold inputs.
- Signal units must be correctly displayed.
- Templates must initialize valid selectable signals.
- Preview and editor must agree on signal identity and availability.
- Preserve stable signal IDs and existing persisted rule definitions.
- Do not silently substitute another device or signal when resolution fails.
- Unknown or stale telemetry must remain distinct from unavailable signal definitions.

## Issue 2 — Grid outage setup labels are unavailable (P2)

Reproduction:

1. Open Automations → Alert rules.
2. Select Set up grid outage alerts.
3. The confirmation dialog shows "an unavailable signal" in both rule descriptions.

### Requirements

- Display correct human-readable signal names.
- Show the source device or installation.
- Show comparison operators and thresholds.
- Display the actual selected signals before confirmation.
- Do not change the underlying grid outage detection semantics.
- Do not create or activate rules during testing.

## Issue 3 — Duplicate signal labels (P2)

Observed:

- Two identical "Heatpump went offline" options.
- Duplicate "reported a fault" entries.
- Duplicate "connectivity" entries.

The Devices page distinguishes the relevant devices as Heatpump and Heatpump Switch, but the Rule Editor does not.

### Requirements

- Investigate whether these are genuinely distinct signals or duplicate catalog entries.
- If they are different signals, display enough identity context to distinguish them.
- Use device custom display names where available.
- Include device/endpoint context when needed.
- If they are true duplicates with the same stable identity, deduplicate them.
- Do not deduplicate distinct signals solely because their labels are equal.
- Do not use display labels as persistent identifiers.
- Preserve stable signal IDs.

## Architecture considerations

Prefer a single canonical signal descriptor shared by catalog, editor, templates and preview.

The descriptor should provide stable identity and presentation metadata.

Reuse the existing capability model and APIs where appropriate.

Do not introduce a parallel signal registry unnecessarily.

Keep frontend and backend semantics consistent.

## Regression tests

Add tests covering:

1. Growatt shared battery Battery SOC selection.
2. Growatt Master AC input voltage selection.
3. Numeric threshold input remains visible.
4. Battery low template initialization.
5. Grid outage setup readable descriptions.
6. Signals from different devices with equal labels.
7. Stable ID preservation.
8. Signal selection after property changes.
9. Preview/editor agreement.
10. Missing signal behavior.
11. Existing persisted rule compatibility.
12. Accessibility of signal selectors.

Run relevant backend and frontend tests and builds.

Report the exact root cause and changed components.

## Constraints

- Do not modify or reset existing Configuration Vault credentials.
- Do not modify production or development database records as part of implementation or testing.
- Preserve existing OAuth flow behavior except for the requested safety improvements.
- Follow project architecture and UI conventions.
- Report any potentially destructive existing backend behavior before changing it.

Implement the change, add tests, and create a local commit. Do not push.

## Execution summary

**Root cause (issues 1 and 2).** The catalog API built each entry's `key` with `AlertSignalReader.Key`. That interpolated the C# `Metric` enum, giving `m:d21:main:BatterySoc`. The browser derived keys from stored `SignalRef`s with `signalKey`, whose `metric` arrives as the camelCase JSON string, giving `m:d21:main:batterySoc`.

Every measurement lookup therefore missed:
- the editor showed "The selected device is no longer available";
- it fell back to an operator list without a threshold input;
- the grid outage dialog and the rule list rendered "an unavailable signal".

Connectivity and event keys contain no metric, so they matched, which is why only measurements failed. The preview never used keys: the server evaluates the `SignalRef` itself, so it read 38% correctly. The frontend tests built their fixture keys in the browser's spelling, which hid the mismatch. Templates were always correct because they copy catalog signals.

**Root cause (issue 3).** The duplicates were distinct signals with different device IDs and therefore different keys. Two current devices shared the same `COALESCE(display_name, name)`, and the catalog label carried only that name. A duplicate row was not possible because `device_state` is keyed by device.

**Changes.**
- **Domain** (`HomeAutomation.Domain/Alerts.cs`):
  - `AlertSignals.Key` and `AlertSignals.MetricName` give the single canonical identity, with metrics in their JSON camelCase spelling.
  - `SignalOwner` and `SignalOwnerNames.Distinguish` keep equal names and append the first distinguishing attribute: model, manufacturer, room, then device number.
- **Infrastructure** (`AlertSignalReader.cs`):
  - The key delegates to the domain.
  - `OwnerNamesAsync` reads the property's current devices once, and the catalog and evaluation evidence share the same names and `MeasurementLabel`.
  - The catalog removes entries with the same key, and only those. Labels that still clash on distinct keys gain their channel.
- **Frontend:**
  - `shared/alerts.ts`: `signalIndex` resolves signals from each entry's own `signal`, tolerating null or absent fields. `signalKey` matches the server.
  - `RuleEditor.tsx`:
    - every lookup goes through the index, and the editor shows the latest reading or *No reading yet*;
    - an unavailable stored signal is kept, flagged `aria-invalid` and described, with its threshold still editable;
    - the preview uses the editor's labels.
  - `AlertRules.tsx`:
    - the grid setup dialog waits for signal names, keeping confirmation disabled meanwhile;
    - switching property clears the open editor, templates and dialogs.
- **No migration.** Stored definitions are unchanged, and keys are not persisted.

**Tests.**
- **Backend:**
  - `AlertRuleEngineTests`: `SignalKeysAreStableAndUseTheJsonSpellingOfMetrics` and `EqualDeviceNamesGainTheFirstAttributeThatTellsThemApart`.
  - `AlertMySqlTests`: `TheCatalogTemplatesPreviewAndStoredRulesShareOneSignalIdentityAndDistinctLabels`.
- **Frontend:** `features/alerts/SignalResolution.test.tsx` has 12 tests covering the 12 requested scenarios.

**Results.**
- **Backend:** Release build with zero warnings. With disposable MySQL 8.0.46 fixtures, 356 passed and 11 skipped, twice in a row; without fixtures, 309 passed and 58 skipped.
- **Frontend:** the build passed and Vitest reported 135 passes.

**Not touched.** No live or development database, vault credential, OAuth flow, running backend or provider was touched. No rule was created or enabled outside the disposable fixture. Nothing was pushed.

**Destructive behavior.** No potentially destructive existing behavior was found or changed.
