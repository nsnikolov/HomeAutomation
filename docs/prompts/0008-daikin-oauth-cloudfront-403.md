# Daikin ONECTA OAuth — Diagnose CloudFront 403

Date: 2026-11-21

## Original prompt

# Daikin ONECTA OAuth — Diagnose CloudFront 403

Investigate the existing Daikin ONECTA OAuth integration.



Current situation:

- Developer application is registered.
- Auth Strategy: Onecta OIDC.
- Client ID and Client Secret are configured.
- Redirect URI is registered and matches the authorization request.
- Database migration completed without visible errors.
- Local authorization and `manage: true` allow starting OAuth.
- The browser reaches `https://idp.onecta.daikineurope.com/v1/oidc/authorize`.
- The authorization request receives HTTP 403 from AWS CloudFront before displaying the login screen.
- Incognito produces the same result.

Current redirect URI:



`http://localhost:5173/api/v1/daikin/callback`



Research whether the official ONECTA OAuth service allows localhost, HTTP redirects, and PKCE S256.



A community implementation (`daikin-controller-cloud`) reports that localhost and 127.0.0.1 redirect addresses are rejected.



Tasks:

1. Inspect the existing OAuth authorization URL generation.
2. Verify the authorization endpoint and required parameters against official Daikin documentation.
3. Check whether scopes, response_type, state, PKCE and redirect_uri are correct.
4. Determine whether the localhost redirect is a likely cause of CloudFront 403.
5. Recommend the simplest secure development configuration if HTTPS or a non-localhost hostname is required.
6. Prefer solutions that preserve the existing browser-bound OAuth state and callback flow.
7. Do not disable PKCE or weaken security without verified provider requirements.
8. Do not log credentials, OAuth authorization codes, or complete authorization URLs containing sensitive parameters.
9. Do not modify credentials, register another developer application, or enable physical device writes.
10. Make only evidence-supported code changes, add tests where appropriate, and document the diagnosis.

If the cause cannot be verified, explicitly identify the uncertainty and recommend the next diagnostic step.



Do not commit or push until the diagnosis is complete.



Report the most likely root cause and the exact changes required.

## Execution summary

- Reproduced the early CloudFront rejection with synthetic authorize requests that held every parameter except the callback URI constant. Localhost over HTTP or HTTPS and HTTPS on the loopback IP returned HTTP 403. Named HTTP and HTTPS callbacks reached Daikin's login proxy with HTTP 302.
- The official ONECTA specification required portal authentication and the public identity metadata checks timed out. The authorization endpoint and scopes match maintained client code. The synthetic S256 probe with the named HTTPS callback cleared the initial edge rejection. Full redirect and PKCE validation still requires a live attempt with the registered app.
- Updated the [ONECTA troubleshooting guide](../integrations/daikin-onecta.md) with the one-origin HTTPS setup and the support information to collect if the rejection persists. Extended [authorization URL tests](../../tests/HomeAutomation.UnitTests/DaikinTests.cs) with synthetic parameters. Runtime OAuth and local credentials were untouched.
- The Release build passed with zero warnings and all 39 backend tests passed. A temporary loopback HTTPS instance validated the fixture hostname, served the React route with HTTP 200, and rejected an unbound callback with HTTP 400. Changes were left uncommitted pending confirmation with the registered app.
