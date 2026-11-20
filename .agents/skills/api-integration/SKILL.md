---
name: api-integration
description: Build or maintain external API clients for Growatt, Daikin ONECTA, and other Home Automation providers, including HTTP transport, authentication, throttling, and provider errors.
---

# API integration

- Verify the provider's authentication flow, request and response contracts, limits, and command replay semantics for the API in use. Register a typed `HttpClient` per provider through `IHttpClientFactory`. Configure base addresses and resilience at registration, keep per-account auth on the request, and avoid capturing typed clients in singleton services.
- For OAuth2 providers, cache tokens by the relevant account, client, and scope. Refresh ahead of expiry with a small clock-skew margin and coordinate concurrent callers so one refresh serves the same token key. Persist rotated refresh tokens through the configured secure store before publishing the replacement token set. Allow at most one refresh and one replay after a token-related `401`, and replay only when the request is safe to repeat.
- Match the local rate limiter to the provider's documented quota scope and window. Use a concurrency limit for caps on in-flight requests. Honor `Retry-After` on `429` and on transient responses that supply it. Otherwise use capped exponential backoff with jitter for eligible transient failures. Bound attempts and total time, including delays, and respect caller cancellation. Surface throttling to the caller when the server's delay exceeds the available budget.
- Retry read operations and writes whose idempotency the provider guarantees. Reuse the same idempotency key across attempts when supported. If a write may have reached the provider before a timeout or disconnect, report an unknown outcome and retain any request identifier that can help the caller reconcile it.
- Map meaningful failures to distinguishable outcomes, including rejected credentials, throttling, invalid responses, and provider unavailability. Preserve useful status and correlation IDs when available. Store local development secrets in .NET User Secrets or the project's equivalent and production secrets in a managed secret store. Keep authorization headers, tokens, and sensitive payload fields out of diagnostics.
