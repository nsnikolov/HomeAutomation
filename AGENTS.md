# Home Automation

This repository targets .NET 10 and C#. The platform integrates Growatt solar inverters and battery storage with Daikin ONECTA air conditioners.

- Follow established repository conventions as the solution evolves. Keep changes scoped to the requested behavior and preserve existing configuration and device behavior unless the change calls for an update.
- Confirm provider-specific capabilities, units, and command semantics against the relevant device or API documentation. Keep credentials and tokens out of source control, fixtures, and logs.
- Use simulated devices for routine development and verification. Once a solution exists, run its relevant `dotnet build` and `dotnet test` commands and report any verification gaps.
- Use the task-specific skills in `.agents/skills/` for C# development, external APIs, automation rules, tests, and reviews.

## Project prompt history

- Before implementing a substantial development request, save its title, date and complete original text, including relevant attachments, under `docs/prompts/`. Use the next sequential filename in the form `0001-short-description.md`. Preserve wording and formatting. Replace passwords, API tokens, credentials and other secrets with `[REDACTED]` before writing the entry.
- Update the [chronological prompt index](docs/prompts/README.md) when adding an entry. Keep each saved entry and its original text permanently. Archive corrections and follow-ups to saved prompts as new entries. Import earlier prompts only from their original text.
- After completion, append a short execution summary and references to the relevant commits or changed files below the saved prompt. Commit the entry and index alongside the related changes.
