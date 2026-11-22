# Daikin Altherma — Inspect Complete ONECTA API Capabilities

Date: 2026-11-22

## Original prompt

# Daikin Altherma — Inspect Complete ONECTA API Capabilities

Investigate the complete set of data exposed by the official Daikin ONECTA Cloud API for the existing device named "Heatpump" (Altherma).



The Home Automation application already has working OAuth authentication, live Daikin device discovery and monitoring.

## Objective

Determine which Altherma measurements, settings, status indicators and capabilities the API actually returns, including fields not currently mapped by our application.

## Tasks

1. Review the existing Daikin adapter and device discovery implementation.
2. Use the existing authenticated API integration to perform one read-only device discovery/status request.
3. Identify the Altherma device by its existing stable provider identity, not only its display name.
4. Inspect the complete nested management points and available characteristic values.
5. Compare the raw provider structure with the current capability mappings.
6. Identify all available temperature measurements and setpoints.
7. Investigate water temperatures, DHW, heating operation, energy measurements, compressor state, defrost status, flow rate, faults and other available capabilities.
8. Distinguish readable measurements from writable settings.
9. Preserve units, provider field paths, supported ranges and data types.
10. Record unsupported and unavailable fields explicitly.

## Important

- Read-only investigation only.
- Respect the 200 requests/day quota.
- Do not execute device commands.
- Do not expose OAuth tokens or personal account information.
- Do not log or commit complete raw API payloads.
- Produce a sanitized inventory of relevant field paths and example values.
- Do not invent unsupported capabilities.
- Do not modify the existing mappings or UI in this task.

## Deliverable

Create:



`docs/integrations/daikin-altherma-capabilities.md`



Include a table containing:

- Provider field path
- Description
- Example value where safe
- Unit
- Readable/writable
- Currently mapped in Home Automation
- Recommended mapping
- Confidence or verification status

Provide a short summary of the most useful additional measurements.



Run only necessary verification checks.



Do not commit or push automatically.

## Execution summary

- Used the existing protected ONECTA connection, quota ledger and typed client for one read-only gateway GET. It returned HTTP 200 and matched Heatpump by its saved gateway and management-point IDs. The raw response was processed in memory.
- The [Altherma capability inventory](../integrations/daikin-altherma-capabilities.md) records the five returned management points, per-characteristic data types and access flags, temperature units and ranges, and available or absent capabilities. The heating energy arrays were present. The sanitizer kept the unit field's type and path but omitted its literal value.
- The temporary inspection helper compiled with zero warnings. The working changes cover documentation and prompt history. Application mappings and UI were not edited. No commit was created.
