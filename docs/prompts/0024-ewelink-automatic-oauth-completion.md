# eWeLink OAuth — Automatic Backend Completion and Token Exchange Diagnostics

Date: 2026-11-23

## Original prompt

# eWeLink OAuth — Automatic Backend Completion and Token Exchange Diagnostics

Continue HomeAutomation from commit fb7b555 or the latest main.



Current issue:

- Cross-site callback HTTP 401 was fixed.
- Manual "Finish connecting" now reaches the backend.
- POST /api/v1/ewelink/1/complete returns 409 {"status":"rejected"}, even when clicked immediately.
- EwelinkClient.ExchangeAsync calls POST /v2/user/oauth/token.
- The upstream rejection reason is currently hidden behind the generic "rejected" status.

## Goal

Complete OAuth automatically in the backend immediately after the eWeLink redirect, without a separate "Finish connecting" button.

## Implementation

1. Inspect the complete existing OAuth flow, session security, EwelinkClient.SendAsync, signature construction, regional host, and official CoolKit OAuth documentation.
2. Preserve SameSite=Strict for the main application session cookie.
3. The callback must validate HTTPS origin, state, correlation cookie, expiry, and single-use semantics before exchanging the code.
4. Bind the flow securely to the original authenticated operator and property using the server-side pending consent record. Do not trust user, property, or authorization data from callback query parameters.
5. Exchange the authorization code immediately in the backend, without requiring the application session cookie on the cross-site callback.
6. Save encrypted tokens only for the validated pending owner, preserving existing access-control invariants.
7. Redirect the browser back to the intended eWeLink property page with an appropriate success/failure indicator.
8. Remove the manual Finish connecting step when automatic completion is verified.
9. Preserve antiforgery protection for authenticated state-changing endpoints. The OAuth callback must remain protected by state, correlation, origin and one-time grant validation.
10. Inspect the actual CoolKit token-exchange request against official documentation: HTTP method, region, App ID header, HMAC signature, content type, body names, and redirect URL.
11. Add safe diagnostics for upstream HTTP status and CoolKit error code, without logging tokens, codes, cookies, secrets, signatures, or full response bodies.
12. Do not automatically retry an authorization code exchange after an ambiguous outcome.

## Tests

Add focused regression tests for:

- Cross-site callback without application session cookie.
- Missing or invalid state/correlation cookie.
- Expired and replayed callbacks.
- Operator/property binding.
- Successful automatic exchange.
- Upstream token rejection.
- No token persistence on failure.

Run backend and frontend builds and relevant tests.



Do not perform live OAuth requests or physical device commands during implementation.



Commit locally, do not push.



Report the exact root cause of "rejected" if established, otherwise identify the remaining live diagnostic needed.

## Execution summary

The callback validates and consumes the browser-bound consent and rechecks the original local operator's property access before exchanging the code. It saves tokens in the existing encrypted vault for the pending owner and returns a local outcome marker. The manual completion step was removed after mocked callback verification. The Strict application session and CSRF checks on local mutations remain in place.

CoolKit documents a 30-second authorization-code lifetime and error 405 on expiry. The regional signed JSON request matches the documented contract. The previous generic rejection did not retain sufficient upstream evidence to establish the cause. Future rejected exchanges record the upstream HTTP status and numeric provider code, when available. No live OAuth calls were made.

The .NET Release and frontend production builds passed. Backend tests recorded 107 passes and three opt-in MySQL skips. All 21 frontend tests passed. Tests verified callback replay protection, operator and property checks, successful encrypted persistence, rejection without token changes, and sanitized upstream details.

See the [eWeLink integration guide](../integrations/ewelink-cloud.md), [callback](../../src/backend/HomeAutomation.Api/EwelinkEndpoints.cs), and [provider client](../../src/backend/HomeAutomation.Infrastructure/Ewelink/EwelinkClient.cs).
