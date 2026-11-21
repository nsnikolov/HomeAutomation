# 0003 — Fix Local Startup and Verify Home Automation Dashboard

**Date** 2026-11-21

## Original prompt

# Task: Fix Local Startup and Verify Home Automation Dashboard

Act as a Senior .NET 10, React, TypeScript and Windows Development Engineer.



The Home Automation application has been implemented and the local MySQL database has been provisioned.



The backend health endpoint is working, but opening [http://localhost:5173](http://localhost:5173) in Chrome results in:



ERR_CONNECTION_REFUSED\
localhost refused to connect.



Your task is to diagnose and resolve the local frontend startup problem, then verify the complete application.

## Current Environment

- Windows 10/11
- Visual Studio Code
- .NET SDK 10.0.401
- Node.js 24.19.0
- npm 11.17.0
- PowerShell requires npm.cmd rather than npm
- MySQL 8.0.46 running locally at 127.0.0.1:3306
- Database: homeautomation
- Runtime credentials configured through .NET User Secrets
- Database migrations and dashboard viewer bootstrap have been executed
- Backend health endpoint is responding successfully
- Docker is not installed

## 1. Repository Review

Read AGENTS.md, relevant Codex Skills, existing architecture documentation, and README.md.



Inspect the current frontend, backend, Vite configuration, VS Code launch settings, and development scripts.



Follow existing conventions.

## 2. Diagnose Frontend Startup

Investigate why [http://localhost:5173](http://localhost:5173) refuses connections.



Check:

- Whether the Vite development server is running.
- Whether npm dependencies are installed.
- Whether the frontend package scripts are correct.
- Whether Vite binds to the expected host and port.
- Whether another process occupies the port.
- Whether the development server exits unexpectedly.
- Whether the VS Code launch tasks correctly start the frontend.
- Whether startup errors are visible in terminal output.

Do not assume the issue requires application code changes.



Prefer identifying the actual cause before modifying configuration.

## 3. Verify Backend and Database

Confirm:

- Backend listens on [http://localhost:5080](http://localhost:5080).
- /health/live responds successfully.
- /health/ready reports the actual database readiness.
- MySQL connectivity works with the configured runtime account.
- Database schema is initialized.
- The simulator telemetry worker runs successfully.
- Database reads and writes succeed.

Do not display or log credentials.



Do not modify database accounts or permissions.



Do not rerun destructive migrations.

## 4. Verify React Dashboard

Start the frontend using the existing supported local development commands.



Use npm.cmd in PowerShell.



Verify:

- [http://localhost:5173](http://localhost:5173) loads the React application.
- The Vite proxy forwards API requests to [http://localhost:5080](http://localhost:5080).
- The sign-in page renders.
- Authenticated dashboard requests work if safe test credentials are available.
- Device and property views load.
- Energy history charts load.
- SignalR connects and receives updates.
- No critical browser console errors occur.

If dashboard credentials are unavailable, stop at the authentication boundary and report that limitation rather than bypassing authentication.

## 5. Improve Developer Experience

If necessary, improve VS Code development launch configurations so both backend and frontend can be started reliably.



Provide a documented workflow for:

- Starting both services.
- Debugging backend.
- Debugging React.
- Stopping both services.
- Handling occupied ports.
- Diagnosing failed startup.

Prefer simple configurations.



Do not add unnecessary dependencies.



Do not require Docker.

## 6. Automated Verification

Run:

- .NET Release build.
- Backend tests.
- Frontend production build.
- Frontend tests.
- Local HTTP smoke checks.

Add a small startup smoke test or script only if it provides clear value and does not duplicate existing tests.



Do not claim successful browser verification unless it was actually performed.

## 7. Scope and Safety

- Do not implement new device integrations.
- Do not change the architecture unnecessarily.
- Do not modify the existing MySQL databases outside homeautomation.
- Do not reset passwords or authentication.
- Do not disable authentication.
- Do not expose development services to external networks.
- Do not commit secrets.
- Avoid changing a working component merely because another component is not running.

## 8. Prompt History

Follow AGENTS.md.



Save this complete prompt as the next sequential file under docs/prompts/.



Update the prompt index and record the execution summary.

## 9. Completion

Provide:

1. Root cause of ERR_CONNECTION_REFUSED.
2. Changes made, if any.
3. Exact commands to start the application.
4. HTTP and browser verification results.
5. Build and test results.
6. Remaining blockers.
7. Recommended next development task.

Commit completed repository changes with a descriptive message.



Do not push to GitHub automatically.



If the only problem is that Vite was not running, do not invent a code fix. Instead verify the startup workflow, improve documentation if needed, and report the correct commands.

## Execution summary

Port 5173 had no listener. The installed Vite dependencies and existing Windows startup command worked after starting the frontend. The API was also idle when checked and was started from its existing launch profile. Both services were left running on loopback. Repository changes cover startup, shutdown, port troubleshooting, and this prompt record.

Direct and proxied health checks returned HTTP 200 with the configured MySQL runtime account. A read-only probe confirmed the simulator persisted a newer telemetry sample. Headless Chrome rendered the sign-in form with no critical console errors. Anonymous property and SignalR requests returned 401. Authenticated dashboard views and SignalR updates remain unverified because a viewer credential was unavailable. The Release build, 13 backend tests, frontend build, and 3 frontend tests passed.

- [Windows startup and troubleshooting](../../README.md)
- [Existing VS Code launches](../../.vscode/launch.json)
