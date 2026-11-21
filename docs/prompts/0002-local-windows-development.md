# 0002 — Prepare Home Automation for Local Windows Development Without Docker

**Date** 2026-11-21

## Original prompt

# Task: Prepare Home Automation for Local Windows Development Without Docker

Act as a Senior .NET 10, React, and MySQL Engineer.



Review the existing repository, AGENTS.md, relevant Codex Skills, architecture documentation, and current implementation.



The application has already been implemented with a .NET 10 backend, React frontend, MySQL persistence, simulator, and telemetry processing.



Your task is to verify and improve the local development experience on Windows without requiring Docker.

## Current Environment

- Windows with Visual Studio Code
- .NET SDK 10.0.401
- Node.js 24.19.0
- npm 11.17.0 (use npm.cmd in PowerShell)
- Existing MySQL service: MySQL80, running
- Docker is not installed

Important: The existing MySQL instance contains other application databases. Do not modify, delete, or reset existing databases or users.

## Tasks

### 1. Local Configuration

Review the backend configuration and document how to use the existing MySQL installation.



Support a configurable database host, port, database name, and credentials.



Do not assume the database runs on port 3307.



Keep development secrets out of source control.



Provide a safe example configuration for an isolated `homeautomation` database.

### 2. Database Setup

Review all SQL migrations for compatibility with MySQL 8.0 and 8.4.



Identify any incompatibilities.



Ensure migration scripts:

- Are repeatable where appropriate.
- Do not affect databases outside Home Automation.
- Use appropriately scoped permissions.
- Fail clearly on errors.

Do not execute destructive SQL.



Do not create or alter MySQL users or databases without explicit approval.



If MySQL credentials are not available, prepare the setup instructions and scripts but do not attempt to guess credentials.

### 3. Backend Startup

Verify that the .NET backend can start directly using dotnet run.



Review:

- Configuration loading
- Connection strings
- Dependency injection
- Database connectivity
- Health checks
- Startup migrations
- Background worker behavior

Improve error messages where configuration is missing.



Do not silently fall back to production credentials or databases.

### 4. React Startup

Verify the frontend using:



npm.cmd ci\
npm.cmd run build



Review Vite development configuration and backend API proxy settings.



Ensure the frontend can connect to the locally running .NET backend.



Document the correct URL for opening the dashboard.

### 5. VS Code Development

Review and improve .vscode/launch.json and tasks.json.



Support:

- Backend debugging
- React development server
- Convenient local startup
- Separate debugging of backend and frontend

Avoid unnecessary extensions or tools.

### 6. Verification

Run all checks that do not require unavailable credentials or external services:

- .NET Release build
- Backend tests
- npm dependency installation
- Frontend production build
- Frontend tests
- Configuration validation
- Static review of SQL migrations

If database startup or end-to-end tests require credentials, document the limitation.



Do not claim tests passed unless they actually executed.

### 7. Documentation

Update README.md with a clear Windows setup guide.



Include:

- Prerequisites
- MySQL configuration
- Database creation instructions
- Migration commands
- Backend startup
- Frontend startup
- Browser URL
- Common troubleshooting steps

Keep Docker as an optional deployment method.

### 8. Prompt History

Follow the existing prompt-history convention in AGENTS.md.



Save this complete prompt in the next sequential Markdown file under docs/prompts/.



Update docs/prompts/README.md.

### 9. Final Deliverables

Provide:

1. Summary of changes.
2. Local startup instructions.
3. Database configuration requirements.
4. Build and test results.
5. Remaining blockers requiring user action.
6. Recommended next steps.

Commit all completed changes with a descriptive commit message.



Do not push to GitHub automatically.

## Important Restrictions

- Do not implement new features or real device integrations.
- Do not install Docker.
- Do not modify existing non-HomeAutomation databases.
- Do not reset MySQL passwords.
- Do not execute destructive database commands.
- Do not request administrator privileges.
- Do not introduce unnecessary architectural changes.

Proceed autonomously with safe repository changes and available verification. If an operation requires credentials, elevated privileges, or risks modifying existing data, skip that operation and document exactly what is required.



Do not stop at analysis only; implement safe improvements and complete all feasible checks.

## Execution summary

Documented Docker-free Windows setup with a dedicated MySQL schema, scoped accounts, and Development User Secrets. Added explicit database configuration validation, a `dotnet run` launch profile, VS Code startup and debugging, and clearer migration and schema-readiness handling.

The .NET Release build, 13 backend tests, clean npm restore, frontend build, and three frontend tests passed. A loopback smoke test verified configuration rejection, direct API startup, Vite proxying, and degraded readiness without MySQL. Static SQL review found a MySQL 8.0.19 minimum for the runtime upsert syntax. Existing migration files were retained. Live migrations and dashboard sign-in require approved schema access and credentials.

- [Windows setup and troubleshooting](../../README.md)
- [Manual MySQL provisioning example](../../database/windows-setup.example.sql)
- [VS Code launch configurations](../../.vscode/launch.json)
