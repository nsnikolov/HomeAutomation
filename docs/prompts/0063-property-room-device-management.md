# HomeAutomation — Property, Room and Device Management, Safe Account Actions and UX Improvements

Date: 2026-11-27

## Original prompt


## Objective

Implement a complete, production-quality property, room and device management workflow for HomeAutomation.

The application currently uses:

- .NET 10 backend with API, Application, Domain and Infrastructure layers.
- React, TypeScript and Vite frontend.
- MySQL database.
- Property-scoped authentication and authorization.
- Configuration Vault with encrypted credentials.
- Provider integrations including Growatt, Daikin ONECTA, eWeLink and Tuya.
- Logical devices, physical devices, provider bindings, telemetry, historical readings and alerts.
- SignalR for real-time updates.

Follow the existing architecture, coding conventions, documentation, domain model and test infrastructure.

The goal is to make HomeAutomation usable with multiple properties, flexible room organization, reliable device ownership, safe device transfers and intuitive administrative actions.

This is an implementation task, not just an architecture proposal.

Work autonomously through investigation, implementation, automated testing, documentation and a local Git commit.

Do not push.

---

# PHASE 1 — Architecture and Data Model Investigation

Before making changes, investigate the existing implementation thoroughly.

Review:

- Current architecture documentation and ADRs.
- Property and room models.
- Device ownership and assignment models.
- Logical devices and physical devices.
- Provider connections and installation relationships.
- Configuration Vault scope semantics.
- User/property authorization.
- Database migrations and seed scripts.
- Simulator initialization.
- Existing frontend Settings, Devices and Dashboard components.
- Existing account actions and OAuth implementation.

## 1.1 Identify default data initialization

Determine exactly where the existing default properties, rooms and installations originate.

Check whether they are created by:

- SQL migrations.
- Seed scripts.
- Application startup.
- Development bootstrap.
- Simulator initialization.
- Test data initialization.

Document the actual source and initialization conditions.

Determine whether restarting the application recreates missing records.

## 1.2 Prevent unwanted default rooms

New properties must not automatically receive any rooms.

Requirements:

- Zero rooms by default.
- Zero assigned devices.
- No automatically generated simulator devices.
- No automatically created provider configuration profiles.
- No automatically created installations unless required by an existing, documented domain invariant.

Do not remove existing properties, rooms or installations during implementation.

If seed data is required for development or tests, isolate it from ordinary property creation.

## 1.3 Preserve established Configuration Vault semantics

Existing configuration model:

- Daikin: installation-wide global account.
- eWeLink: installation-wide global account.
- Tuya: installation-wide global account.
- Growatt: property-scoped configuration.

Global profiles use scope_id = 0.

Property profiles use their respective property scope.

There is no automatic inheritance from global configuration to property configuration.

Do not change these rules.

Do not expose secret values or global identifiers to unauthorized property users.

---

# PHASE 2 — Property Management

Implement complete property management through the backend and React UI.

## 2.1 Property list

Provide an interface showing all properties accessible to the authenticated user.

Display:

- Property name.
- Optional description.
- Number of rooms.
- Number of associated devices.
- Relevant provider configuration status.
- Active property indicator.

Do not expose inaccessible properties.

Provide an obvious Add property action.

Use the existing application layout and styling.

## 2.2 Create property

Implement property creation with sensible defaults.

Fields:

- Name: required.
- Description: optional.
- Timezone: default Europe/Sofia, unless existing project conventions specify otherwise.

A newly created property must contain:

- Zero rooms.
- Zero devices.
- Zero default logical devices.
- No automatically generated simulator data.
- No automatically created provider profiles.

Validate names and input lengths.

Avoid creating duplicate properties accidentally through double submissions.

Respect existing authorization rules.

Do not automatically give unrelated users access to newly created properties.

Ensure the creator can access the property only through the appropriate existing authorization workflow.

## 2.3 Edit property

Allow authorized users to edit:

- Property name.
- Description.
- Timezone, if supported by the existing model.

Editing property metadata must not change device ownership, telemetry or provider configuration.

## 2.4 Delete property

Implement safe property deletion.

**The only business condition that blocks property deletion is the existence of associated devices.**

Rules:

- Property with zero devices: deletion is allowed.
- Property with one or more devices: deletion is blocked.
- Existing rooms must not prevent deletion.
- Existing empty installations must not prevent deletion.
- The user must not be required to delete rooms manually before deleting a property.

Before deletion, display:

- Property name.
- Number of rooms.
- Number of devices.
- Number of installations.
- Any relevant configuration or authorization dependencies.

Require typing the exact property name to confirm deletion.

The final Delete action must remain disabled until confirmation is valid.

Backend requirements:

- Check device ownership atomically during deletion.
- Prevent race conditions with concurrent device assignments.
- Never cascade-delete devices or historical telemetry.
- Handle property-owned rooms and safe metadata cleanup transactionally.
- Preserve auditability.
- Respect authorization.
- Prevent duplicate deletion requests.

Important:

Configuration Vault credentials must never be silently deleted.

Investigate the lifecycle of property-owned configuration profiles and other dependent records.

If deletion conflicts with preserved credentials or historical ownership, report the exact conflict and implement a safe, documented mechanism that satisfies the business rule without destroying credentials or historical records.

Do not weaken database constraints just to force deletion.

---

# PHASE 3 — Room Management

Rooms are optional organizational entities.

A property must be fully usable with zero rooms.

## 3.1 Room CRUD

Implement:

- List rooms.
- Create room.
- Rename room.
- Edit description.
- Delete room.

Validate:

- Required room name.
- Property ownership.
- User authorization.
- Input lengths.
- Duplicate submissions.

Avoid unnecessary complexity and additional abstractions.

## 3.2 Room deletion behavior

Deleting a room must not delete its devices.

When a room is deleted:

- Remove current device-to-room assignments.
- Keep devices assigned to their property.
- Preserve provider bindings.
- Preserve telemetry.
- Preserve device state.
- Preserve historical alerts.
- Preserve historical audit events.

The operation must be transactional.

Do not leave invalid room references.

## 3.3 Empty state

When a property has no rooms, show an appropriate empty state:

"No rooms yet. Add your first room."

Do not display fake or automatically generated rooms.

---

# PHASE 4 — Remove Existing Device-to-Room Associations

The existing devices have room assignments that are no longer desired.

Implement a controlled, one-time administrative operation to clear these relationships.

Scope:

- All currently property-associated devices.
- All relevant logical device representations.
- All current device-to-room assignments.

Requirements:

1. Remove only room relationships.
2. Keep devices associated with their current property.
3. Preserve physical device identities.
4. Preserve logical device identities.
5. Preserve telemetry and historical readings.
6. Preserve provider bindings.
7. Preserve installation associations.
8. Preserve Configuration Vault credentials.
9. Preserve relevant audit history.
10. Do not delete the rooms themselves.

First investigate whether room assignments exist in multiple tables or representations.

Avoid clearing only one relationship while leaving inconsistent related data.

## 4.1 Safe execution

Implement:

- Dry-run mode.
- Report of affected properties, devices and assignments.
- Explicit execute mode.
- Confirmation requirement.
- Database transaction.
- Rollback on failure.
- Idempotent behavior.

Do not automatically execute this operation.

Do not run it against the existing development or production databases.

Provide the exact command for manual execution after the implementation has been reviewed.

Document its expected results.

---

# PHASE 5 — Cross-Property Device Transfers

Implement the ability to move an already assigned device from one property to another.

Example:

Property 1 → Property 2

This is a first-class domain operation, not a combination of manual unassign and assign operations.

## 5.1 Transfer workflow

Allow an authorized user to:

1. Open a device's contextual actions.
2. Select Move to property.
3. Choose a destination property.
4. Review the transfer impact.
5. Confirm the transfer.
6. Receive success or error feedback.

Show the current and destination properties clearly.

Do not allow moving to the same property.

Require appropriate access to both properties.

## 5.2 Transfer semantics

During transfer:

- Preserve the permanent device identity.
- Preserve provider device identifiers.
- Preserve capabilities.
- Preserve valid provider bindings.
- Preserve device state.
- Preserve historical telemetry.
- Preserve historical alerts.
- Preserve audit history.
- Do not duplicate devices.
- Remove any current room assignment.
- Update current property relationships consistently.
- Update dependent logical device and installation relationships where necessary.

Inspect the domain model carefully before deciding which relationships must move.

Do not create invalid cross-property foreign keys.

Do not silently transfer or duplicate Configuration Vault credentials.

Global provider accounts must remain global.

Property-scoped provider connections may require additional validation or explicit rebinding. Handle these dependencies without changing account scope semantics.

## 5.3 Historical ownership — important

Moving a device must not rewrite history as if the device had always belonged to the destination property.

Example:

An electricity meter belongs to Property 1 for six months and is then moved to Property 2.

Expected behavior:

- Historical consumption from the first six months remains attributable to Property 1.
- New consumption belongs to Property 2.
- Device identity and telemetry remain preserved.
- Historical reports must not accidentally transfer old energy consumption into Property 2.

Investigate whether the current telemetry model stores property identity at sample level or resolves it through the current device assignment.

If historical attribution currently depends only on the latest property assignment, identify this architectural issue before implementing transfer.

Implement a robust and efficient approach, such as time-bounded ownership history, if appropriate.

Avoid unnecessarily rewriting large telemetry datasets.

Consider:

- Telemetry queries.
- Hourly rollups.
- Energy aggregation.
- Alerts.
- Device history views.
- Dashboard aggregation.
- Existing reporting APIs.

Ensure the solution remains efficient with large telemetry tables.

## 5.4 Atomicity and concurrency

Device transfer must be atomic.

Requirements:

- Validate current ownership.
- Validate destination.
- Validate authorization.
- Prevent concurrent conflicting transfers.
- Roll back if any related operation fails.
- Prevent duplicate assignment records.
- Emit appropriate state changes after commit.

Use existing transaction and persistence patterns.

---

# PHASE 6 — Device Assignment UX

Improve device assignment controls without making initial device assignment harder.

## 6.1 Unassigned devices

For devices not associated with any property:

**Keep the existing visible "Assign here" button and current behavior.**

Do not hide it.

Do not move this initial assignment action into an overflow menu.

## 6.2 Devices already assigned to the current property

For devices assigned to the selected property:

- Do not display "Assign here".
- Clearly show the current property.
- Show the current room only if assigned.
- Keep ordinary device monitoring and controls prominent.

## 6.3 Devices assigned to another property

For devices already assigned elsewhere:

- Do not show a prominent "Assign here" button.
- Provide a subtle contextual action.
- Prefer a three-dot menu.
- Label the action "Move to this property" or similarly.
- Require explicit confirmation.
- Use the transfer workflow defined above.

Avoid changing the visual design unnecessarily.

## 6.4 Room assignment

Room assignment must remain optional.

Devices can exist without rooms.

Room assignment controls should be secondary to normal device monitoring.

Do not force room selection when assigning a device to a property.

---

# PHASE 7 — Custom Device Names

Add support for user-defined device display names.

Examples:

- Heat Pump
- Kitchen AC
- Solar Inverter
- Main Electricity Meter
- Boiler
- Outdoor Temperature Sensor

Requirements:

- Preserve the original provider name.
- Preserve the original provider identifier.
- Allow users to define a custom display name.
- Use the custom name as the primary label when present.
- Fall back to the provider name when no custom name exists.
- Allow clearing the custom name.
- Do not change provider identifiers when renaming devices.

Custom names should survive:

- Provider synchronization.
- Discovery refresh.
- Property transfer.
- Room reassignment.
- Application restart.

Do not let provider discovery overwrite user-defined names.

Reuse existing domain fields if they already support this behavior.

Avoid adding duplicate naming models.

---

# PHASE 8 — Device Transfer Audit History

Record important device lifecycle events.

At minimum:

- Initial property assignment.
- Property transfer.
- Property unassignment where supported.
- Room assignment.
- Room removal.
- Custom name changes.

For property transfers, capture:

- Device identity.
- Previous property.
- New property.
- User who initiated the action.
- Timestamp.
- Outcome.

Avoid recording secret or sensitive provider data unnecessarily.

Reuse existing audit infrastructure where appropriate.

Provide access to relevant history in the device details interface if consistent with existing UI architecture.

Do not build an entirely new generic event-sourcing framework.

---

# PHASE 9 — Daikin ONECTA Account Action Safety

Improve the existing Daikin ONECTA controls.

## 9.1 Account action menu

Current actions include:

- Refresh discovery.
- Reconnect account.
- Disconnect account.

Keep Refresh discovery visible.

Move Reconnect account and Disconnect account into a compact Account actions menu.

Prefer an accessible three-dot menu.

Follow the existing design system.

## 9.2 Disconnect confirmation

Selecting Disconnect account must open a confirmation modal.

The modal must:

- Identify Daikin ONECTA.
- Explain the consequences of disconnecting.
- Explain that synchronization may stop.
- Mention that reauthorization may be required.
- Accurately describe whether device metadata and history are preserved.
- Require typing exactly `DISCONNECT`.
- Disable the final destructive button until the text matches.
- Provide Cancel.
- Prevent accidental double submission.
- Display success and failure states.

Do not initiate disconnect before explicit confirmation.

## 9.3 Reconnect confirmation

Reconnect account must also display a confirmation modal.

Explain that the current authorization may be replaced or refreshed.

Require explicit confirmation, but do not require typing text.

Then continue through the existing OAuth reconnect flow.

Preserve callback behavior and token handling.

## 9.4 Backend safety

Verify existing backend authorization.

Do not rely only on frontend confirmation.

Inspect the current disconnect implementation for destructive behavior before changing it.

Disconnect must not unexpectedly delete:

- Device metadata.
- Device ownership.
- Telemetry history.
- Historical alerts.
- Configuration Vault records.
- Other unrelated application data.

If the current backend behavior is destructive, report the exact effects before changing it.

Preserve auditability.

Never log access tokens, refresh tokens or stored credentials.

---

# PHASE 10 — Reusable Confirmation System

Create or extend a reusable confirmation modal that supports:

- Ordinary confirmation.
- Destructive confirmation.
- Typed confirmation.
- Asynchronous execution.
- Loading state.
- Error state.
- Cancel behavior.
- Keyboard accessibility.
- Focus management.
- Prevention of repeated submission.

Reuse it for:

- Disconnect account.
- Reconnect account.
- Delete property.
- Move device.
- Other high-impact actions where appropriate.

Keep domain-specific operations outside the generic modal.

Do not introduce a large new UI framework.

---

# PHASE 11 — Dependency Review Before Deletion

Introduce clear impact information before destructive actions.

For property deletion, show relevant counts and dependencies.

For device transfer, show:

- Source property.
- Destination property.
- Room assignment consequences.
- Potential provider or installation constraints.

For room deletion, show:

- Room name.
- Number of currently assigned devices.
- Explanation that devices will remain in the property.

Do not expose secret values.

Backend validation remains authoritative.

Do not assume the frontend's cached counts are current.

---

# PHASE 12 — Automatic UI State Synchronization

After administrative operations, the UI must reflect the updated state without requiring a browser reload.

Cover:

- Property creation.
- Property editing.
- Property deletion.
- Active property changes.
- Room creation.
- Room deletion.
- Room renaming.
- Device assignment.
- Device transfer.
- Device renaming.
- Provider connection status changes.

Update relevant views:

- Dashboard.
- Devices.
- Settings.
- Energy.
- History.
- Property selector.
- Room selector.

Use existing frontend data-fetching and state-management conventions.

Use SignalR where appropriate, but do not introduce unnecessary real-time complexity.

Prevent stale cached property ownership from appearing after transfers.

Handle the case where the currently selected property is deleted.

Ensure the user is redirected to an appropriate remaining property or a useful empty state.

---

# PHASE 13 — Authorization and Property Isolation

Review and apply the existing authorization model consistently.

Requirements:

- Users only see properties they can access.
- Property creation requires the appropriate permission.
- Property deletion requires the appropriate permission.
- Room management requires appropriate property permissions.
- Device transfers require authorization for both source and destination properties.
- Device rename requires appropriate permissions.
- Global Configuration Vault profiles remain protected.
- Ordinary property users must not gain global administrator privileges.

Enforce authorization in the backend.

Do not implement permission checks only in React.

Reuse existing role and permission abstractions.

Do not invent a parallel authorization system.

Add negative authorization tests.

---

# PHASE 14 — Error Handling and Reliability

All new operations must provide predictable behavior.

Handle:

- Unauthorized user.
- Missing property.
- Missing device.
- Concurrent deletion.
- Concurrent device transfer.
- Duplicate submission.
- Database transaction failure.
- Invalid destination property.
- Device already assigned to destination.
- Invalid room reference.
- Provider connection constraints.
- API timeout.
- Network interruption.

Use appropriate HTTP status codes.

Return useful error messages without exposing internal implementation details.

Do not silently ignore failures.

Do not report success until the backend confirms the operation.

---

# PHASE 15 — Automated Testing

Build a comprehensive regression test suite using the existing testing infrastructure.

## Backend tests

Cover:

- Creating a property with zero rooms.
- Creating a property with zero devices.
- No automatic simulator initialization.
- Property metadata editing.
- Property deletion when no devices exist.
- Property deletion with rooms but no devices.
- Property deletion blocked when devices exist.
- Concurrent assignment and property deletion.
- Room CRUD.
- Safe room deletion.
- Devices without rooms.
- Clearing existing room assignments.
- Device transfer across properties.
- Transaction rollback.
- Concurrent device transfer attempts.
- Device identity preservation.
- Telemetry preservation.
- Historical property attribution.
- Configuration Vault scope preservation.
- Custom device names.
- Provider synchronization preserving custom names.
- Property authorization.
- Global administrator authorization.
- Daikin disconnect/reconnect authorization.

## Frontend tests

Cover:

- Property list.
- Add property.
- Edit property.
- Delete property confirmation.
- Room creation and deletion.
- Correct empty states.
- Visible "Assign here" for unassigned devices.
- Hidden "Assign here" for already assigned devices.
- Contextual transfer action for devices in another property.
- Transfer confirmation.
- Custom device naming.
- Daikin account actions menu.
- Typed DISCONNECT confirmation.
- Cancel behavior.
- Incorrect confirmation text.
- Loading and error states.
- Double-submit prevention.
- State refresh after operations.

## Integration tests

Use isolated, disposable test databases.

Include tests proving that:

- Existing provider credentials remain unchanged.
- Device transfers do not duplicate records.
- Property deletion cannot remove an associated device.
- Room deletion cannot remove historical telemetry.
- Historical energy attribution survives a device transfer.
- Global configuration remains isolated from property configuration.

Do not use the existing development or production database for tests.

Run the relevant backend tests, frontend tests and production builds.

Report test counts, failures and skipped tests.

---

# PHASE 16 — Documentation

Update the relevant existing documentation.

Include:

- Property lifecycle.
- Room lifecycle.
- Device assignment and transfers.
- Historical telemetry ownership.
- Custom device naming.
- Configuration Vault interaction.
- Permission requirements.
- Protected account actions.
- Database migrations.
- One-time room-assignment cleanup instructions.
- Operational rollback considerations.

Update the project handoff documentation.

Store the implementation prompt in the existing prompt archive using the next available numbering convention.

Avoid unnecessary documentation duplication.

---

# PHASE 17 — Implementation and Delivery Strategy

Work autonomously in the following order:

1. Inspect architecture and current implementations.
2. Identify existing database and domain constraints.
3. Report important findings and any destructive backend behavior.
4. Design minimal changes consistent with the existing architecture.
5. Implement necessary additive migrations.
6. Implement backend domain operations.
7. Implement authorization and validation.
8. Implement frontend management screens.
9. Implement protected confirmation dialogs.
10. Implement historical ownership and audit behavior.
11. Implement isolated database tests.
12. Run complete relevant test suites and builds.
13. Update documentation.
14. Review changes for data safety, correctness and security.
15. Create a local Git commit.

You may divide the implementation internally into manageable steps.

Do not stop after completing just the first feature.

Do not ask for routine implementation decisions when existing architecture provides a clear answer.

Prefer straightforward, maintainable solutions over unnecessary abstraction.

If an existing schema or behavior makes a requested operation unsafe, explain the specific conflict and choose the safest architecture-consistent solution. Never silently delete data or weaken authorization.

Avoid unrelated refactoring.

Maintain backward compatibility where practical.

Ensure the application builds and tests pass before committing.

---

# Mandatory Constraints

- Do not modify or reset existing Configuration Vault credentials.
- Do not modify production or development database records as part of implementation or testing.
- Preserve existing OAuth flow behavior except for the requested safety improvements.
- Follow project architecture and UI conventions.
- Report any potentially destructive existing backend behavior before changing it.

Implement the change, add tests, and create a local commit. Do not push.

---

# Final Report

After implementation, provide a structured report containing:

1. Root cause and location of existing default rooms.
2. Architecture and data model changes.
3. New database migrations.
4. Implemented property-management features.
5. Implemented room-management features.
6. Device transfer behavior and historical ownership guarantees.
7. Device naming and audit features.
8. Daikin account-action safety changes.
9. Frontend UX improvements.
10. Authorization and security validation.
11. Test results, including passed, failed and skipped counts.
12. Commands required for any manual administrative operation.
13. Known limitations or follow-up work.
14. Local Git commit hash.

Explicitly confirm:

- Existing credentials were preserved.
- Existing development and production database records were not modified.
- Existing OAuth functionality was preserved.
- No changes were pushed to the remote repository.

## Follow-up

please continue
## Execution summary

Implemented in the commit that adds this entry. See [property, room and device management](../architecture/16-property-room-device-management.md).

- **Default data:** only migration 002 seeds Countryside House, its three rooms, the `solar` installation and the `simulator` connection; the migration ledger prevents reseeding. Growatt ingest creates its installation only for the configured site-key property. New properties are created empty.
- **Destructive behavior reported before change:** Daikin **Disconnect** clears the stored ONECTA token envelope and marks the account disconnected. Devices, telemetry, alerts and vault profiles are untouched. The behavior is unchanged; only the confirmation was added. No existing endpoint hard-deleted properties, devices or telemetry, and the new code does not either.
- **Backend:** migration 020; `PropertyManagementStore` and `/property-management`, `/details`, `/deletion-impact`, `/delete`, `/room-management`, `/devices/{id}/name|room|audit|transfer-impact|transfer`, `/transferable-devices`. Soft property deletion is excluded from every membership check. Provider assignments are audited and cannot land in a deleted property. Device moves retire the source row and keep history with the source property. The `--clear-room-assignments` dry-run-first command is added. Readers use custom names and exclude retired rows.
- **Frontend:** Settings property and room management; device menu (rename, room, move); **Devices in your other properties**; **Save room** versus **Assign here** on provider pages; Daikin account menu with typed `DISCONNECT`; device audit; shared `ConfirmDialog` and `ActionMenu`. Settings is lazy-loaded.
- **Verification:**
  - **Backend:** the Release build passed with zero warnings. Fixture runs reported 331 passed and 11 skipped, twice in a row, after one intermittent 7D timing failure that passed alone. Without fixtures, 295 passed and 47 skipped.
  - **CLI smoke:** dry run, refusal, execute and idempotent rerun on the disposable schema.
  - **Frontend:** build passed; Vitest reported 109 passed.
- **Safety:** existing vault credentials were not modified. No development or production database was used; all database tests and the CLI smoke ran on a disposable loopback MySQL 8.0.46. The OAuth flows are unchanged. Nothing was pushed.
- **Manual steps on deployment:** apply 017–020 with the migration account after a backup, then run `GRANT DELETE ON <schema>.rooms TO <runtime account>`, then restart. Run `--clear-room-assignments` only if wanted, dry run first.
