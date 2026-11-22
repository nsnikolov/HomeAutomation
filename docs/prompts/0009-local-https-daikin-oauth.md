# Home Automation — Local HTTPS for Daikin OAuth

Date: 2026-11-21

## Original prompt

# Home Automation — Local HTTPS for Daikin OAuth

Configure a secure local HTTPS development origin for Home Automation to support Daikin ONECTA OAuth.

## Confirmed configuration

- Windows hosts file already maps `ha-dev.britishlab.org` to `127.0.0.1`.
- Ping resolves successfully.
- Existing React/Vite development server uses port 5173.
- Existing .NET backend uses port 5080.
- New HTTPS development origin: `https://ha-dev.britishlab.org:5443`
- Daikin OAuth callback: `https://ha-dev.britishlab.org:5443/api/v1/daikin/callback`

## Requirements

1. Configure Vite to serve HTTPS on port 5443 using a locally trusted certificate.
2. Prefer mkcert for local certificate generation.
3. Bind the development server to loopback only.
4. Keep the existing backend on port 5080.
5. Proxy `/api` and health requests securely to the backend.
6. Preserve existing application authentication, cookies, CSRF protection and Daikin OAuth state correlation.
7. Do not modify the production BritishLab website or public DNS.
8. Do not expose the application to the internet.
9. Keep certificate private keys outside Git and exclude them from source control.
10. Preserve the existing `Daikin:TokenKey`, Client ID and Client Secret.
11. Update `Daikin:RedirectUri` to the new HTTPS callback using .NET User Secrets.
12. Document the Windows development setup and commands.
13. Verify HTTPS access, local login, authenticated API requests and OAuth callback routing.
14. Do not execute real Daikin device commands.
15. Run relevant tests and build checks.
16. Preserve previous uncommitted OAuth diagnostic changes and tests.

Do not silently install system-wide tools or trust a new certificate authority without explaining the required action.



Do not commit or push until the HTTPS configuration is verified.



Report any remaining manual steps needed in Daikin Developer Portal.

## Execution summary

- Configured Vite's `dev:https` mode on `127.0.0.1:5443` and restarted the Development backend on its existing port 5080. The port 5173 workflow continues to respond. The HTTPS origin forwards the browser scheme only on the trusted local hop. Host validation covers HTTP/1.1 and HTTP/2.
- Generated the certificate using portable mkcert and added its dedicated CA to the current Windows user's Root store. The keys are under LocalAppData. Updated `Daikin:RedirectUri` and verified the three original Daikin credential settings remained identical.
- Verified HTTPS with Windows trust and signed in through Chrome using the existing local account. Session and property requests returned 200. The session cookie was Secure, HttpOnly and SameSite=Strict. Authenticated ONECTA start returned the configured HTTPS callback, PKCE S256 and a Secure, HttpOnly, SameSite=Lax correlation cookie. An unbound callback returned 400.
- Release and frontend production builds passed, along with 43 backend and seven frontend tests. The existing Daikin portal registration still needs the exact callback URI and a consent retry. The browser check stopped before the external authorization page. The changes and earlier OAuth diagnostics remain uncommitted.

See [Local HTTPS for ONECTA on Windows](../../README.md#local-https-for-onecta-on-windows), [Vite configuration](../../src/frontend/home-automation-web/vite.config.ts), and [proxy and login tests](../../tests/HomeAutomation.IntegrationTests/ApiTests.cs).
