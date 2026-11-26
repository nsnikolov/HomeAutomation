# HomeAutomation — Milestone 7E: Automatic Logical Device Management

Date: 2026-11-26

## Original prompt

# HomeAutomation — Milestone 7E: Automatic Logical Device Management

Implement Milestone 7E: automatic logical-device provisioning, assignment management, and complete logical-device management through the existing React UI.

This milestone builds on the architecture introduced in Milestones 7A–7D.

The goal is to make logical devices the natural way users organize and monitor their equipment, without requiring manual CLI initialization whenever a new physical device is discovered.

This is a production-oriented implementation. Preserve historical integrity, provider isolation, authorization, and all existing functionality.

Use extended/high reasoning for data-model consistency, concurrency, lifecycle management, and migration compatibility.

## 0. Read project context

Before implementation:

1. Read `CLAUDE.md`, `AGENTS.md`, and `docs/AI_HANDOFF.md`.
2. Review the latest commits and current Git status.
3. Read the relevant architecture documentation:
   - `docs/architecture/10-historical-telemetry.md`
   - `docs/architecture/11-logical-devices.md`
   - The existing eWeLink continuous collection design.
4. Review the logical-device schema, services, APIs, and frontend components.
5. Inspect device discovery and reconciliation for all providers.
6. Inspect existing property authorization, room management, audit logging, and dashboard source resolution.
7. Identify existing initialization and assignment operations that can be reused.

Relevant commits:

- 7A Historical Telemetry: `754615e`
- 7B Logical Devices: `35838fa`
- 7C Historical Charts: `33a2c5c`
- 7D Continuous eWeLink Collection: `73bc40a`

Migrations 017 and 018 may not yet be applied to the live development database.

Do not assume the live database is migrated.

Preserve the .NET 10, Dapper, MySQL, React/TypeScript, SignalR, and Configuration Vault architecture.

Produce a concise implementation plan before editing code.

## 1. Main objective

Currently, existing devices can be initialized into logical devices using an explicit CLI command.

However, newly discovered physical devices may remain without logical identities until the initialization command is run again.

Implement automatic logical-device provisioning and a complete management workflow.

Users must be able to:

- See all logical devices in a property.
- See physical devices that are not assigned to logical devices.
- Create logical devices manually.
- Rename logical devices.
- Edit descriptions and rooms.
- Assign an available physical device.
- Replace an assigned physical device.
- View previous physical assignments.
- Unassign a device safely.
- Archive a logical device.
- Restore an archived logical device where appropriate.
- Resolve auto-created logical devices when the hardware is intended to replace another device.

The design must preserve history and avoid confusing logical identity with physical hardware identity.

## 2. Automatic provisioning

When a newly discovered physical device is successfully persisted and associated with a property, automatically create an appropriate logical identity unless the device is already covered by an existing logical assignment.

Examples:

- A new Tuya thermometer receives a logical device.
- A new eWeLink/Tongou power meter receives a logical device.
- A new Daikin unit may receive a logical device.
- A newly discovered Growatt inverter follows existing installation and binding rules.

Use the existing provider-neutral device inventory and discovery/reconciliation pipeline.

Do not implement separate provisioning logic in each provider unless unavoidable.

### Requirements

- Provision exactly once per physical device and property.
- Use stable physical inventory identifiers.
- Be idempotent.
- Handle repeated provider discovery.
- Handle concurrent discovery.
- Avoid duplicate logical devices after application restarts.
- Do not create a new logical identity for every endpoint.
- Do not create duplicate logical devices for multiple telemetry source streams from the same physical device.
- Reuse the existing logical-device creation and assignment services.
- Respect property boundaries.
- Preserve existing devices and their assignments.
- Do not overwrite user-customized names or rooms.
- Do not automatically replace another physical device merely because its name, type, or room is similar.

The operation must be transactionally safe.

A provisioning failure must not corrupt or permanently block the normal physical-device discovery process.

Use bounded retries and diagnostics where appropriate.

## 3. Default logical identities

Create sensible default names based on existing device metadata.

Examples:

- Tuya Temperature Sensor.
- Sonoff Power Meter.
- Tongou Relay.
- Daikin Heat Pump.

Where possible, retain useful inventory names without generating duplicates that are confusing in the UI.

Do not invent room assignments.

If the physical device already has a known room association, reuse it.

Otherwise leave the logical device unassigned to a room.

Allow users to rename and organize devices later.

Mark auto-provisioned identities as system-created when the existing schema supports this or when a small, justified schema extension is needed.

Avoid unnecessary new tables or migrations.

## 4. Historical identity and initial assignment

New auto-provisioned logical devices must follow Milestone 7B assignment semantics.

Physical assignments use half-open UTC intervals:

`[valid_from_utc, valid_to_utc)`

### First discovery

Determine the correct assignment start time using reliable information.

If the physical device already has historical telemetry, preserve access to its existing measurements.

If it is genuinely new, use a well-defined start time.

Do not fabricate an observation timestamp.

Preserve the existing initialization semantics where compatible.

Document the chosen policy clearly.

### Existing manually initialized devices

Do not change existing logical assignments.

Do not recreate existing logical identities.

Do not alter historical replacement intervals.

### Hardware replacement

If a newly discovered device is intended to replace an older one, the user must be able to attach it to the existing logical identity.

The software must not guess that two devices are replacements based on names or similar capabilities.

Replacement must remain explicit and audited.

## 5. Auto-created identities and replacement resolution

Consider this scenario:

1. Tongou Relay A is assigned to "Heat Pump Power Meter".
2. The user installs Sonoff Relay B.
3. Device discovery automatically creates a new logical device for Relay B.
4. The user wants Relay B to replace Relay A while retaining the original history.

Implement an intuitive workflow to resolve this.

Suggested approach:

- Show Relay B as a newly discovered device.
- Allow selecting "Use as replacement for an existing logical device".
- Preview compatible measurements.
- Confirm the replacement effective time.
- Close the old physical assignment.
- Attach Relay B to the existing logical device.
- Resolve the temporary auto-created logical identity safely.

Requirements:

- Preserve both devices' physical telemetry.
- Preserve the original logical device's historical identity.
- Do not automatically merge unrelated histories.
- Do not rewrite raw telemetry or hourly rollups.
- Do not delete the temporary logical device's history without explicit confirmation.
- If the temporary logical identity has its own meaningful history, explain the consequences and require appropriate user confirmation.
- Avoid showing duplicate logical devices unnecessarily after successful replacement.
- Keep existing dashboard references safe.
- Do not silently delete or retarget saved widgets.

Any reparenting, archival or removal of an auto-created logical identity must be auditable.

## 6. Logical-device management UI

Extend the existing Devices section rather than creating an unrelated administrative application.

Provide a clear management interface.

### Logical device list

Display:

- Logical device name.
- Room/location.
- Device type or role.
- Current physical device.
- Provider.
- Available measurements.
- Assignment status.
- Auto-created or manually created status, if supported.
- Archived status where relevant.

Allow filtering by:

- Room.
- Provider.
- Device kind.
- Assignment status.
- Archived/active.

Avoid an unnecessarily complex table.

### Logical device details

Allow authorized users to:

- Rename.
- Edit description.
- Change room.
- View measurement capabilities.
- View physical assignment history.
- Assign or replace hardware.
- Unassign hardware.
- Archive and restore.

Keep historical readings accessible for archived logical devices.

### Unassigned devices

Provide a separate view or filter listing physical devices without an active logical assignment.

Allow users to:

- Create a logical identity for a physical device.
- Assign it to an existing compatible logical device.
- Inspect its available capabilities.

Do not assume an unassigned physical device is broken or offline.

### Device replacement

Reuse the workflow implemented in 7B.

Avoid creating a second competing replacement implementation.

Keep the effective timestamp explicit.

Display any capability losses or incompatibilities.

Require confirmation for actions affecting historical identity.

## 7. Manual logical-device creation

Implement logical-device creation in the UI using existing backend APIs.

Support:

- Name.
- Optional description.
- Optional room.
- Device kind or role where appropriate.
- Optional initial physical device assignment.

Allow creating an empty logical device without physical hardware.

An empty logical device must not show fabricated telemetry.

History should show an appropriate unassigned state.

Allow a physical device to be assigned later.

## 8. Editing and lifecycle operations

Implement appropriate management APIs or reuse existing ones.

### Rename and metadata updates

- Preserve logical-device ID.
- Preserve historical assignments.
- Preserve history URLs.
- Preserve dashboard logical-source keys.
- Do not rewrite raw telemetry.

### Unassignment

- Close the active assignment at a specific effective time.
- Preserve older assignments.
- Leave the logical device unassigned.
- Do not delete historical data.
- Do not automatically create a new logical device for the unassigned hardware unless explicitly intended.

### Archive

- Hide archived logical devices from normal active-device lists.
- Preserve historical queries.
- Preserve audit history.
- Avoid destructive deletion.

### Restore

- Restore the logical device's active management visibility.
- Do not silently reactivate an old physical assignment.
- Require explicit assignment if no valid active hardware is present.

Apply appropriate conflict handling and concurrency checks.

## 9. Authorization and auditing

Follow existing property and role conventions.

Owner and Admin may:

- Create logical devices.
- Edit metadata.
- Assign devices.
- Replace hardware.
- Unassign devices.
- Archive and restore.

Other authorized users may view logical devices and their histories according to existing read permissions.

Enforce authorization server-side.

Enforce property isolation at the database and service boundaries.

Audit all changes affecting logical identity, assignment, or lifecycle.

Include:

- Property ID.
- Logical device ID.
- Operation.
- Acting user or system identity.
- Previous and new physical assignment where relevant.
- Effective timestamp.
- Operation timestamp.
- Reason where required.

For automatic provisioning, use a clearly distinguishable system actor.

Do not log secrets or provider credentials.

## 10. Dashboard compatibility

Preserve the logical dashboard source identifiers introduced in 7B.

Requirements:

- Renaming a logical device does not break dashboard widgets.
- Changing room does not break history or dashboard bindings.
- Replacing hardware updates logical-source resolution.
- Archiving must not silently delete widgets.
- Unassigned logical devices show an unavailable state.
- Retired hardware values must not appear as current.
- Temporary auto-created identities must not leave broken widget references after resolution.

Preserve existing physical-source dashboard widgets.

Do not automatically convert user widgets without authorization or an explicit migration policy.

If resolving a temporary logical identity affects widget references, present a safe, explicit review step.

## 11. Provider compatibility and safety

Preserve all providers:

- Growatt.
- Daikin ONECTA.
- eWeLink / Sonoff / Tongou.
- Tuya.
- Simulator.

Do not change provider authentication, polling, or electrical collection behavior.

### Growatt

Existing Growatt physical-identity validation must remain intact.

Automatic logical provisioning must not weaken the existing inverter serial-number guard.

An inverter replacement requiring provider binding reconfiguration remains a separate workflow.

Do not interpret a new Growatt serial as permission to accept a different inverter automatically.

### eWeLink

Milestone 7D introduced continuous electrical collection, disabled by default.

Do not enable it in this milestone.

Do not change its reporting interval or quota behavior.

Do not send `uiActive` or other device commands as part of this milestone.

### Configuration Vault

Do not modify encrypted credentials, OAuth grants, key files, or provider source selections.

## 12. Discovery and reconciliation safety

Inspect actual device-discovery lifecycles.

Avoid generating logical identities for transient, incomplete, duplicate, or rejected devices.

Provision only after the physical device has been accepted into the established inventory.

Do not interpret an offline device as a removed device.

Do not automatically archive logical devices merely because a provider is temporarily unavailable.

Prevent repeated discovery from modifying historical assignment start times.

Keep provisioning failures isolated from telemetry persistence where possible.

Provide structured diagnostics that do not flood logs.

## 13. Database migration

Inspect the existing schema before deciding whether a migration is required.

Prefer reusing migration 018's logical-device tables.

If additional persistent metadata or constraints are essential, create the next available migration, expected to be 019.

Do not modify migrations 014–018.

Use the existing migration runner and checksum tracking.

Do not apply migrations to the live development database.

Do not assume migrations 017 or 018 are already applied there.

Use disposable MySQL databases for tests.

Document any schema or permission changes.

## 14. Tests

Add focused backend and frontend tests.

### Auto-provisioning

- New physical device.
- Repeated discovery.
- Concurrent discovery.
- Multiple endpoints per physical device.
- Multiple sources for one physical metric.
- Discovery after backend restart.
- Existing logical assignment.
- Historical physical device with existing telemetry.
- Provider offline.
- Failed provisioning and retry.
- Cross-property isolation.
- Growatt identity validation remains intact.

### Management

- Manual logical-device creation.
- Metadata updates.
- Room changes.
- Initial assignment.
- Unassignment.
- Archive.
- Restore.
- Historical visibility after archival.
- Authorization failures.
- Transaction conflicts.
- Audit completeness.

### Replacement resolution

- Auto-created logical identity followed by hardware replacement.
- Tuya to eWeLink sensor replacement.
- Tongou to Sonoff power-relay replacement.
- Capability mismatch.
- Replacement inside an hour.
- Historical telemetry continuity.
- No counter bridging.
- No duplicate logical histories.
- Safe handling of temporary identities.
- Existing dashboard references preserved.

### Frontend

- Logical-device list.
- Filters.
- Create and edit forms.
- Unassigned physical devices.
- Assignment workflow.
- Replacement workflow reuse.
- Archive and restore.
- Loading and error states.
- Concurrent-edit conflicts.
- Property-level authorization.

### Regression

- Existing telemetry ingestion.
- 7A hourly aggregates.
- 7B logical histories.
- 7C charts.
- 7D eWeLink continuous collection defaults.
- Existing dashboards.
- Existing provider integrations.

Use synthetic device data, mocked providers and disposable database instances.

Never call real providers or operate physical devices during testing.

## 15. Implementation sequence

Proceed in the following order:

1. Review current logical-device and discovery architecture.
2. Identify reusable 7B services and operations.
3. Implement safe automatic provisioning.
4. Add any necessary schema changes.
5. Extend lifecycle and assignment APIs.
6. Implement manual creation and editing UI.
7. Implement unassigned-device management.
8. Implement replacement resolution for auto-created identities.
9. Add archive/restore and assignment-history UI.
10. Validate dashboard and history continuity.
11. Implement tests.
12. Update documentation and handoff.

Keep changes incremental and maintainable.

If the scope becomes too large, stop at a stable, tested boundary and document remaining work.

## 16. Scope boundaries

Do not implement:

- New historical chart types.
- New provider integrations.
- eWeLink collection changes.
- Physical device configuration.
- Physical-device deletion.
- Device pairing.
- New automation rules.
- Energy calculation changes.
- Data retention or deletion.
- Bulk device migration without explicit authorization.
- Unrelated dashboard redesign.
- Unrelated refactoring.

Do not automatically proceed to another milestone.

## 17. Deployment safety

The user cannot currently perform live deployment and verification.

Therefore:

- Do not apply migrations to the live MySQL database.
- Do not run logical initialization against the live database.
- Do not restart the backend.
- Do not touch the running application.
- Do not call provider cloud APIs.
- Do not send commands to devices.
- Do not enable continuous eWeLink collection.
- Do not modify Configuration Vault credentials or keys.
- Do not push commits.

Tests must run only with mocks or disposable infrastructure.

Document the deployment procedure for later.

## 18. Completion requirements

At completion:

1. Summarize automatic logical-device provisioning.
2. Explain how duplicate creation is prevented.
3. Explain how auto-created identities are resolved during hardware replacement.
4. Describe the management UI.
5. List new APIs and migrations.
6. Report authorization and auditing behavior.
7. Explain dashboard and historical continuity.
8. Report backend and frontend builds and tests.
9. Identify unresolved assumptions or limitations.
10. Document safe deployment steps.
11. Update relevant architecture documentation and ADRs.
12. Update `docs/AI_HANDOFF.md`.
13. Archive this prompt under the next available number in `docs/prompts`.
14. Commit locally with a descriptive message.
15. Do not push.

The implementation must simplify device management while preserving historical integrity and all existing HomeAutomation functionality.

## Execution summary

**Provisioning**
- A provider-neutral sweep (`LogicalProvisioningWorker`, every `LogicalDevices:ProvisionIntervalMinutes`, default 5; `LogicalDevices:AutoProvision` default on) creates one `automatic` identity per accepted device that has never had a logical assignment or origin and reports a measurement.
- Only properties that already use logical devices take part, so the dry-run-first initialization stays the onboarding step.
- It reuses the initialization plan and its assignment semantics: per-endpoint channels only for repeated measurements, and a start at the history origin, so existing readings stay attributed and no time is invented.
- **Exactly once:** the property row lock plus the unique origin key. Released or unassigned hardware is never re-provisioned.
- **Failures** back off per property (5 minutes up to 6 hours).
- **Names:** the inventory name, de-duplicated with the model and then the device number. The device's room is reused.
- **Actor:** `system:provision`.

**Schema:** migration 019 adds `logical_devices.creation`, with an `initialized`/`manual` backfill, and the `provisioned`, `unassigned`, `restored` and `resolved` audit actions. Logical readiness now requires 017–019. No grant change.

**API**
- Optional hardware on create, `expectedUpdatedAt` on edit, `/unassign`, `/restore`, `/physical-devices` and `/{id}/release-preview`.
- Assign and Replace take `releaseFrom`, `acceptReleasedHistory` and `archiveReleased`.
- New outcomes `releaseRequired` (409) and `historyConfirmation` (400).

**Resolution:** hardware in use by another identity is released only when the user names that identity. The other identity's open assignment closes at the exact time (audit `resolved`), with optional archiving. Readings before that time stay with it, and keeping them needs explicit confirmation. Raw telemetry, rollups and dashboard widgets are never rewritten; widgets bound to an archived identity show "archived".

**UI:** the Devices page's Logical devices section now has:
- filters (room, provider, kind, assignment, active or archived) and creation badges;
- create, edit, assign, replace (the 7B form plus a release preview), unassign, archive and restore;
- an **Unassigned and new hardware** view with "Create logical device" and "Use as replacement for an existing logical device".

**Verification**
- **Backend:** Release build passed with zero warnings. Tests reported 302 passes and 11 opt-in skips with disposable MySQL 8.0.46 fixtures, twice in a row; without fixtures, 281 passes and 32 skips.
- **Frontend:** production build passed and Vitest reported 90 passes.
- **Not touched:** the live database, the running backend, the providers and the devices.

**References**
- [Logical device management](../architecture/14-logical-device-management.md)
- [README deployment sequence](../../README.md#logical-devices-and-device-replacement)
- `database/migrations/019_logical_device_management.sql`
- `src/backend/HomeAutomation.Infrastructure/MySql/LogicalDeviceStore.cs` and `LogicalProvisioning.cs`
- `src/frontend/home-automation-web/src/features/LogicalDevices.tsx`
