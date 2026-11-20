# ADR 0002 — React SPA

**Status** Proposed

## Context

The product is an authenticated dashboard with live state, historical charts and interactive controls. Public search indexing and server-rendered landing pages are outside its initial scope.

## Decision

Use a React/TypeScript SPA built with Vite and served from the API origin in production. Use Tailwind and shadcn/ui, Recharts, TanStack Query and SignalR. Keep routing and UI interaction in the browser and all authorization and device decisions in ASP.NET Core.

## Consequences

One static artifact works in both deployment modes with a simple cookie and CSRF model. The SPA requires JavaScript and API access for interactive screens, and it owns loading and reconnect behavior. Server rendering would add a runtime or server view layer with limited benefit for these workflows. Revisit if the product gains substantial public content or first-load requirements. [Frontend architecture](../03-frontend-architecture.md)
