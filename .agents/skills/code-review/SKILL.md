---
name: code-review
description: Review a Home Automation change for defects in correctness, security, maintainability, and performance. Use for requested reviews and report actionable findings by severity.
---

# Code review

- Read the diff and trace affected entry points, configuration, callers, and tests. Look for concrete failures that the change introduces or exposes under supported inputs and deployment conditions.
- Check overlapping polling and command dispatch, token refresh races, cancellation and shutdown, DI lifetime mismatches, and disposal of timers, scopes, streams, and HTTP responses. Look for unbounded work, excessive provider calls, or blocking on asynchronous work when they have a measurable impact.
- Trace what happens to credentials and provider payloads through errors and logs. Check whether invalid or missing telemetry, an expired override, or a timeout with an unknown command outcome can lead to unsafe, repeated, or conflicting device actions.
- Flag missing tests when a specific behavior or regression is unverified and consequential. Tie maintainability findings to a concrete cost, such as inconsistent behavior across callers or duplicated rules that can drift.
- List findings highest severity first. Use Critical for credible safety incidents, credential exposure with broad impact, or severe data loss. Use High for major operational failure, unsafe device control, or exploitable security defects. Use Medium for bounded reliability or correctness failures and Low for minor but actionable issues. For each finding, give a file and line, the triggering condition, and its impact. If there are no findings, say so and note material verification gaps.
