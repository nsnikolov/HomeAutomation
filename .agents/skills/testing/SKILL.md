---
name: testing
description: Add or maintain xUnit unit and integration tests for Home Automation, including simulated provider behavior and timing, concurrency, and failure scenarios.
---

# Testing

- Use xUnit and follow the repository's test conventions as they emerge. Unit-test rule decisions from supplied inputs and assert the observable action or absence of one. Use theories for meaningful boundary cases and assert external calls when their order or count affects behavior.
- Control time and scheduling through the code's existing seams or a `TimeProvider` test double. Advance time for freshness, hysteresis, override expiry, retry budgets, and shutdown. Coordinate competing calls with deterministic signals when testing races.
- Fake provider interfaces for rule and application tests. For API adapter tests, use a scripted `HttpMessageHandler` or a loopback server to verify request format, response mapping, rate limiting, and token refresh with synthetic credentials and payloads.
- Use integration tests when correctness depends on real composition, such as resolving DI scopes, binding options, handling worker shutdown, or persisting state across restarts. Keep storage and device simulation isolated per test and clean them up even when assertions fail.
- Choose edge cases from the changed behavior. Include exact thresholds, stale or out-of-order data, outage and recovery, manual override races, and duplicate or ambiguous commands for affected automation. For affected integrations, exercise timeouts, throttling, refresh failures, malformed responses, and cancellation where they change the outcome.
