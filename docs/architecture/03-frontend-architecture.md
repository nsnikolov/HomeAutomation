# Frontend architecture

Build a React and TypeScript SPA with Vite, Tailwind CSS and shadcn/ui, served on the API's origin in deployed environments. Use Recharts for historical data and TanStack Query for server state. Add React Router for nested routes and deep links. The application shell owns navigation, authentication state, the property picker, and the live connection status. See [ADR 0002](adr/0002-react-spa.md).

```text
home-automation-web/src/
├── app/                 # Shell, routes, session and query providers
├── features/
│   ├── properties/
│   ├── dashboard/
│   ├── devices/
│   ├── integrations/
│   ├── history/
│   ├── automation/
│   └── alerts/
└── shared/              # HTTP, SignalR, formatters and accessible UI primitives
```

## Routing and state

- `/login` and `/properties` handle sessions and property selection. `/properties/:propertyId` owns dashboard, devices, device detail, history, automation, and alerts. Capability, authorization and integration health drive control availability. The server enforces permissions even when a route is entered directly.
- Put shareable selections such as time range, chart series and resolution in the URL. Keep transient form and modal state local. Key TanStack Query entries with user session and property context as appropriate, plus filters and resolution. Clear the cache on session change and cancel obsolete reads on property switches. Query results are the application view of server truth.
- Use one typed HTTP boundary for same-origin cookie requests, CSRF on mutations, cancellation and `ProblemDetails` parsing. Derive or check TypeScript transport contracts against OpenAPI once the API exists. Handle `401` with session recovery, `403` with an access state, and partial provider errors at panel level. A root error boundary handles unexpected rendering failures.
- A control submission carries an idempotency key for that user intent. Keep a draft setpoint local, show the backend's queued and pending states, and update the reported value only from fresh server readings. Show rejected, unverified and unknown outcomes with a link to command details. Avoid silent mutation retries and offer a status check before a new control request after a lost response.

## Devices and controls

- Group the device overview by property and room, with an unassigned section. Filter by device type, capabilities and status. Show one entry per physical device. Detail pages expose channels, recent readings, last contact, integration health, command history, active rules and override scope or expiry.
- Device-card collections store order per authenticated user, property, page and group. Assigned cards precede unassigned candidates. A dedicated pointer and keyboard handle keeps ordering separate from actions on a card. Tuya visibility uses the same preference scope and keeps hidden cards available for restoration.
- Compose device cards and detail panels from a typed catalog. Render `Switch` as an explicit On/Off control for each supported channel, `PowerMeasurement` as a watts readout and trend, `EnergyMeasurement` as kWh over the selected period, and temperature and humidity as shared sensor views. HVAC mode and setpoint controls use their capability-specific constraints. Provider names appear in provenance and integration settings. Use the same panels across manufacturers.
- Render read-only measurements and display unavailable control reasons such as unsupported, disabled, permission required, provider unavailable, or pending verification. Switch controls show last reported state, pending requested state and uncertainty distinctly. For commissioned loads show the authorized operation window, runtime deadline when known, and current restrictions. Never present a queued or accepted OFF request as confirmed isolation.
- The admin integration view leads users through provider-hosted consent or server-side provisioning, candidate review, import/linking, friendly names and room assignment. It shows sync time, credential status and mapping issues. Provider secrets and tokens never enter browser state or API responses. Keep the rule editor to forms for supported triggers, conditions and actions, with execution and dry-run history.

## Live data and dashboard

- Maintain one authenticated SignalR connection per loaded app. Subscribe only to the current property. Use push messages to invalidate targeted query keys with coalescing and ignore late messages from a previous property. On reconnect, resubscribe and refetch visible state and pending command status. If the connection is down, keep last-known values with age and run a low-frequency REST refresh that reads stored data. [SignalR JavaScript client](https://learn.microsoft.com/en-us/aspnet/core/signalr/javascript-client?view=aspnetcore-10.0) · [TanStack Query invalidation](https://tanstack.com/query/latest/docs/framework/react/guides/query-invalidation)
- Put grid status, PV production, household consumption, battery SOC and power flow on the overview. Link to installation and inverter detail so users can see what a total covers. Show appliance and circuit consumption as named breakdowns, identifying meters that overlap the household total. Add room temperature and humidity charts and show individual sensor sources, gaps and sample ages.
- Read bounded, server-aggregated series for large ranges. Recharts plots sampled numeric series, draws gaps for missing data, and uses stepped lines for state transitions. Label metric units, time zone, source and partial coverage. Provide a tabular view for key values and accessible status labels that work without color.
- Display `Unknown`, `Stale`, `Offline` and explicit faults distinctly. Show a read-only history view when control is unavailable. Alert badges and a command timeline explain degraded behavior without blocking healthy devices.

## Frontend verification

Use Vitest and React Testing Library with mocked API and live events. Cover property switching during in-flight requests, access denial, partial data and chart gaps, reconnect recovery, capability rendering shared across providers, independent relay channels, and a switch command that stays pending until readback. Keep chart tests focused on displayed data and accessibility rather than rendering internals.
