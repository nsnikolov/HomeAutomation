# eWeLink OAuth — Diagnose HTTP 401 on Callback

Date: 2026-11-23

## Original prompt

# eWeLink OAuth — Diagnose HTTP 401 on Callback

The eWeLink OAuth flow reaches the registered callback:



[https://ha-dev.britishlab.org:5443/api/v1/ewelink/callback](https://ha-dev.britishlab.org:5443/api/v1/ewelink/callback)



However, the browser receives HTTP 401 after eWeLink redirects back with code, region=eu and state.



Inspect:

- EwelinkEndpoints.cs
- Authentication cookie configuration
- SameSite and Secure cookie policies
- HTTPS proxy and forwarded-header handling
- Existing Daikin OAuth callback implementation

Determine whether the callback request reaches the endpoint or is rejected by authentication middleware.



Fix the root cause while preserving:

- OAuth state validation
- Correlation cookie validation
- Operator authorization
- Property authorization
- CSRF protection
- Secure token storage

Do not simply remove authorization without implementing an equivalent secure validation mechanism.



Add focused regression tests covering the OAuth callback and cross-site browser redirects.



Do not log authorization codes, state values, cookies or tokens.



Run relevant backend tests and build.



Commit locally. Do not push.



Do not send live OAuth requests during implementation.



Keep the fix minimal and report the exact cause and verification results.

## Execution summary

ASP.NET Core challenged the original callback before the handler ran because the top-level cross-site GET omits the Strict application session cookie. The callback now validates the HTTPS origin, region, expiring state and browser correlation cookie, then redirects to the property's eWeLink page. A same-site completion POST checks authentication, operator and property authorization, CSRF and original browser/user binding before exchanging the grant once. Token storage and proxy configuration were preserved.

The .NET Release and frontend production builds passed. Backend tests recorded 104 passes and three opt-in MySQL skips. All 21 frontend tests passed. The callback regression simulated the cross-site cookie set and checked trusted forwarding, replay rejection and completion permissions. No live OAuth requests were made.

See the [eWeLink integration guide](../integrations/ewelink-cloud.md), [callback and completion endpoints](../../src/backend/HomeAutomation.Api/EwelinkEndpoints.cs), and [API regression tests](../../tests/HomeAutomation.IntegrationTests/ApiTests.cs).
