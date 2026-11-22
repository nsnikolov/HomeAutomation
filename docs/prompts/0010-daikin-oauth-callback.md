# Home Automation — Daikin ONECTA OAuth Callback Investigation and Fix

Date: 2026-11-22

## Original prompt

# Home Automation — Daikin ONECTA OAuth Callback Investigation and Fix

Act as a Senior .NET 10 Engineer, OAuth2/OIDC Security Specialist, and React Engineer.



Investigate and resolve the Daikin ONECTA OAuth callback failure in the existing Home Automation application.



**IMPORTANT: Codex usage is nearly exhausted (approximately 6% remaining). Work incrementally, prioritize diagnosis, and preserve all findings so this task can be resumed in the same session later.**

## 1. Project Context

Repository: `C:\Git\HomeAutomation`



Technology stack:

- .NET 10 modular monolith
- React + TypeScript + Vite
- MySQL 8.0.46 with Dapper
- Cookie-based application authentication
- Daikin ONECTA Cloud integration using OAuth2
- Existing Growatt V4 integration
- Background telemetry workers

Read AGENTS.md, relevant Codex Skills, architecture documentation, and existing Daikin integration documentation.



Preserve all existing functionality.

## 2. Current OAuth Configuration

The Daikin Developer Portal application uses:

- Application name: HomeAutomation
- Authentication strategy: Onecta OIDC
- OAuth authorization endpoint: `https://idp.onecta.daikineurope.com/v1/oidc/authorize`
- PKCE: S256
- Authorization code flow

The application is now running through a local HTTPS development origin:



`https://ha-dev.britishlab.org:5443`



The registered and configured redirect URI is:



`https://ha-dev.britishlab.org:5443/api/v1/daikin/callback`



The hostname resolves locally to `127.0.0.1` through the Windows hosts file.



The site is not publicly hosted.



The existing Vite proxy forwards API requests to the .NET backend.

## Execution summary and resume point

- The live HTTPS health endpoint returned 200. The same origin routed an unbound callback to the backend and returned 400 as expected. The existing backend log contained no ONECTA callback or token-exchange evidence from a real attempt. The local probe does not establish where the reported provider attempt stopped.
- The source now preserves the local confirmation route after sign-in and removes the confirmation marker after a successful completion request. Callback diagnostics record only a fixed reason or acceptance. They never write the request query, authorization code, state, cookie, or provider error text.
- Added a fixture for a correlated callback that redirects to the local confirmation route without carrying the authorization code. The .NET and frontend verification commands were rejected before execution because automatic approval review reached its usage limit. The running backend was not restarted for these changes.
- Resume by running the backend tests and the frontend build/tests, then start the updated backend on port 5080 and make a fresh consent attempt from the HTTPS origin. A provider-page failure, a callback 400, and a failure after **Finish connecting** are separate stages. For a callback rejection, capture only its HTTP status and the fixed `Reason` in the `ONECTA callback rejected` event.

See [callback diagnostics](../../src/backend/HomeAutomation.Api/DaikinEndpoints.cs), [confirmation page](../../src/frontend/home-automation-web/src/features/DaikinPanel.tsx), and [callback tests](../../tests/HomeAutomation.IntegrationTests/ApiTests.cs).
