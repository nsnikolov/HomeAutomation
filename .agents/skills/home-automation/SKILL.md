---
name: home-automation
description: Design or change Home Automation rules for grid outages, Growatt battery state of charge, and Daikin temperature control, including manual overrides, safe fallback, and command arbitration.
---

# Home automation

- Evaluate rules from a timestamped snapshot of grid, battery, temperature, and device state. Have rules return proposed actions with reasons. Resolve proposals by explicit priority before passing the winning action for dispatch.
- Model grid state as available, outage, or unknown. Use a trusted grid signal and configurable confirmation periods for loss and recovery. Keep cloud/API loss and stale or missing telemetry distinct from an outage. Suspend rules that require a known grid state while the signal is uncertain.
- Normalize battery state of charge (SOC) to percent and track its freshness. Reject out-of-range readings and apply configured reserve and recovery thresholds to avoid cycling around a boundary. Treat stale or unavailable SOC as unknown and use the configured fallback for rules that depend on it.
- Apply separate start and stop thresholds for heating or cooling, with explicit units and boundaries. Respect device-supported modes, configured minimum runtime and restart intervals, and current device state when choosing a new action.
- Apply device and battery safety constraints before manual overrides and automatic rules. Define override scope and expiry or explicit release. When the override ends or telemetry recovers, reevaluate fresh state before dispatch. During uncertainty, follow the configured safe behavior for the device and suppress discretionary changes.
- Dispatch through one coordination path that serializes commands per device across overlapping rules and workers. Compare the proposed target with confirmed state and pending commands, and recheck priority, freshness, and override state before sending. If an outcome is unknown, reconcile device state before reissuing. On worker restart, rebuild state needed for safe dispatch before resuming automation.
