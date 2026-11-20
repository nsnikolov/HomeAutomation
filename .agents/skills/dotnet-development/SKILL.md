
---
name: dotnet-development
description: Implement, refactor, and maintain .NET 10 and C# application code for the Home Automation platform. Use for application architecture, dependency injection, configuration, background workers, state management, logging, and lifecycle management.
---

# .NET Development

## Architecture and Code Organization

- Follow the existing solution structure, conventions, and build settings.
- Apply Clean Architecture principles pragmatically. Separate domain logic from hosting, persistence, and external integrations.
- Introduce new projects, interfaces, or abstractions only when they establish meaningful boundaries or improve testability.
- Keep provider-specific DTOs and protocols within integration layers. Expose application-owned models across boundaries.
- Prefer composition over inheritance.
- Follow SOLID principles where they improve maintainability. Avoid unnecessary abstraction and overengineering.
- Preserve existing public contracts, configuration compatibility, and persisted data when refactoring.

## Dependency Injection

- Register dependencies in the composition root.
- Prefer constructor injection and explicit dependencies.
- Choose service lifetimes according to ownership and thread-safety requirements.
- Never inject scoped services directly into singleton services.
- BackgroundService instances must create and dispose scopes when accessing scoped dependencies.
- Allow the DI container to manage and dispose container-owned services.

## Configuration

- Use strongly typed options with the Options pattern.
- Validate required settings and cross-field constraints during startup.
- Fail fast when configuration essential to an enabled feature is invalid.
- Keep secrets out of source control and application logs.
- Separate configuration from runtime state.
- Use IOptionsMonitor only when runtime changes can be applied safely.

## Asynchronous Programming

- Use async/await for asynchronous I/O.
- Propagate CancellationToken through asynchronous operations.
- Avoid blocking async code with .Result, .Wait(), or unnecessary Task.Run().
- Await application-owned tasks and observe failures.
- Use ConfigureAwait only when justified by the execution context.
- Ensure timers, streams, scopes, and other disposable resources are released correctly.

## Background Services

- Use BackgroundService for continuously running processes.
- Prefer PeriodicTimer for recurring polling when appropriate.
- Respect cancellation and support graceful shutdown.
- Handle transient failures without permanently terminating an otherwise healthy worker.
- Do not silently swallow exceptions.
- Distinguish transient, permanent, and configuration-related failures.
- Use bounded retry policies and backoff where appropriate.
- Prevent overlapping execution when a task must run sequentially.
- Ensure one failed device operation does not unnecessarily stop unrelated device processing.
- Define explicit behavior for unexpected fatal worker exceptions.

## Time and Scheduling

- Use TimeProvider for application time and testable delays.
- Store timestamps in UTC.
- Use DateTimeOffset for timestamps representing real instants.
- Convert to local time only at presentation or scheduling boundaries.
- Handle time zones and daylight-saving transitions explicitly.
- Prefer elapsed-time measurement for durations when wall-clock adjustments could affect correctness.
- Avoid relying directly on DateTime.Now or DateTime.UtcNow in business logic.

## State Management

- Model meaningful application and device states explicitly.
- Distinguish unknown, unavailable, stale, and valid states.
- Avoid treating missing data as zero or as a valid device reading.
- Define allowed state transitions and recovery behavior.
- Ensure state shared across concurrent workers is accessed safely.
- Persist state only when recovery or business requirements justify it.
- Avoid hidden mutable global state.

## Reliability and Idempotency

- Design operations to tolerate retries, restarts, and duplicate events.
- Prefer idempotent operations when communicating with external systems.
- Avoid sending unnecessary repeated device commands.
- Confirm operation results when the integration supports confirmation.
- Never assume that a command was executed merely because an HTTP request succeeded.
- Handle partial failures explicitly.
- Do not invent device state when external systems are unavailable.

## Logging and Diagnostics

- Use structured ILogger message templates with named properties.
- Include relevant operation, device identifier, and outcome information.
- Preserve exception context.
- Log errors at the boundary responsible for handling them.
- Avoid duplicate exception logging across layers.
- Keep routine polling logs low-volume.
- Never log credentials, access tokens, or sensitive configuration.
- Expose health and diagnostic information where operationally useful.

## Code Quality and Performance

- Enable nullable reference types.
- Prefer readable, maintainable code over clever optimizations.
- Use explicit result types for expected failures when callers need to distinguish outcomes.
- Avoid unnecessary allocations in frequently executed paths.
- Avoid premature optimization.
- Keep public APIs consistent and predictable.
- Prefer small, cohesive classes with clearly defined responsibilities.
- Write code that can be tested without real external devices.

## Completion Criteria

Before completing implementation work:

1. Verify the solution builds successfully.
2. Run relevant existing tests.
3. Add or update tests when behavior changes.
4. Check for compiler warnings introduced by the change.
5. Verify cancellation, error handling, and resource disposal where applicable.
6. Summarize changed behavior and any remaining risks.
7. Clearly report tests or checks that could not be executed.
