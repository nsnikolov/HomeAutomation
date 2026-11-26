# Logical device management

Milestone 7E makes logical devices manageable from the **Devices** page and covers newly discovered hardware automatically. It extends the [7B model](11-logical-devices.md) without changing its interval rules or any existing assignment. Migration 019 is the only schema change.

## Schema (migration 019)

- `logical_devices.creation` records how an identity came to exist: `manual`, `initialized` (the CLI) or `automatic` (provisioning). Existing identities with an `initialized` audit row are backfilled; others become `manual`.
- The audit action check gains `provisioned`, `unassigned`, `restored` and `resolved`. MySQL 8.0.19 or later is needed for `DROP CHECK`.
- No new table and no grant change. The existing runtime account (`SELECT`, `INSERT`, `UPDATE`) is sufficient; nothing is deleted.
- Logical-device readiness now requires 017, 018 and 019. Before 019, logical routes return 503 and provisioning waits. Other features are unaffected.

## Automatic provisioning

- **Where.** `LogicalProvisioningWorker` sweeps every `LogicalDevices:ProvisionIntervalMinutes` (default 5), one minute after startup. It reads the provider-neutral inventory (`devices`), so no provider has provisioning code. It runs outside telemetry ingestion: a failure never blocks discovery or persistence.
- **Which properties.** Only properties that already have at least one logical device, archived ones included. The explicit, dry-run-first initialization stays the onboarding step, so deploying this version never bulk-creates identities.
- **Which devices.** A device accepted into the inventory that:
  - has never had any logical assignment;
  - is not the origin of any logical identity;
  - reports at least one measurement capability (status-only devices are skipped).

  Hardware that was unassigned, released or replaced keeps its "ever assigned" state and never gets a new identity. Offline devices are still inventory devices; provisioning never archives anything.
- **What.** One identity per physical device, with the same channel plan as initialization: one `main` channel, or one channel per endpoint when a measurement repeats across endpoints. Several telemetry streams of one device are one identity. Assignments start at the history origin (1970-01-01), as initialization does. All recorded observations of the device stay attributed to it, and no observation time is invented. The device's room is reused; none is invented.
- **Names.** The inventory name when no active logical device has it, otherwise `Name (Model)`, then `Name #id`. An empty name becomes manufacturer and kind, for example "Tongou Relay" or "Daikin Heat Pump". Users can rename later; provisioning never edits an existing identity.
- **Exactly once.** Each property is provisioned in one transaction that locks the property row, and `ux_logical_device_origin (property_id, origin_device_id)` rejects a second identity. Repeated discovery, restarts and concurrent sweeps therefore create each identity once. Start times are never rewritten.
- **Failures.** A failing property backs off for 5 minutes, doubling up to 6 hours; others continue. Logs: one information line when identities are created; a warning with the property ID and error type on failure; a one-time note while migrations are pending. The audit actor is `system:provision`.
- **Providers.** Provisioning writes only logical tables. It never touches provider bindings, so the Growatt serial guard is unchanged: an unexpected inverter is never accepted into the inventory in the first place. Provider authentication, polling and 7D electrical collection (still off by default) are untouched; no device command is sent.

## Management operations

| Operation | API (`/api/v1/properties/{id}/logical-devices`) | Rules |
| --- | --- | --- |
| List (with archived) | `GET ?includeArchived=true` | Any member. Includes `creation`. |
| Physical devices | `GET /physical-devices` | Any member. Current logical device, its creation and `everAssigned` per device. |
| Create | `POST` | `deviceId` is now optional: an identity without hardware has no assignment and shows no readings. |
| Edit | `PUT /{id}` | Name, kind, room, description. Optional `expectedUpdatedAt` turns a concurrent edit into 409. ID, assignments, history URLs and dashboard keys are unchanged. |
| Assign | `POST /{id}/assignments` | For a channel without an active device. |
| Replace | `POST /{id}/replace` | The 7B workflow and its capability checks. |
| Unassign | `POST /{id}/unassign` | `expectedAssignmentId` and an explicit time after the assignment started. Closes the interval and leaves the identity empty. |
| Archive | `POST /{id}/archive` | Unchanged: ends open assignments now and hides the device from the active list. History and audit stay. |
| Restore | `POST /{id}/restore` | Shows the device again. Never reopens an assignment; hardware must be assigned explicitly. |
| Release preview | `GET /{id}/release-preview?deviceId&endpointId&effectiveAt` | 204 when no other identity uses the hardware at that time. |

Owner and Admin change; other members read. Every change locks the property row and is audited with the actor, the action, the old and new device, the effective and operation times, and the reason.

## Resolving an auto-created identity

When new hardware B was auto-created as L_B but actually replaced A on L_A:

1. **Unassigned and new hardware** lists B as recording into an auto-created identity. **Use as replacement for an existing logical device** opens L_A's replacement form (or its assignment form, if L_A is empty) with B preselected.
2. Assign and Replace refuse hardware that another identity uses (`releaseRequired`, 409), unless `releaseFrom` names that identity. The software never infers a replacement from names, rooms or capabilities.
3. The preview shows:
   - the readings of B before the chosen time, which stay with L_B;
   - whether L_B would be left without hardware;
   - how many dashboard measurements reference L_B.
4. If readings would stay with L_B, `acceptReleasedHistory` is required (`historyConfirmation`, 400). Choosing an earlier time keeps them on L_A instead.
5. In one transaction:
   - L_B's open assignment of B is closed at the time (audit `resolved`);
   - optionally L_B is archived (audit `archived`; the UI pre-selects this for an auto-created identity left empty);
   - L_A changes over at the same instant (audit `replaced` or `assigned`).

Nothing is deleted or moved. Raw samples and hourly rollups are unchanged; each identity reads only inside its own intervals, so counters are never differenced across devices (7B segment rules). L_B keeps its earlier readings and stays readable, including when archived.

## Dashboards and history

- Logical source keys `logical:{id}:{channel}:{Metric}` are unchanged by rename, room changes, restore and resolution.
- Replacing hardware moves resolution to the new device, and a reading taken before its assignment never appears as current.
- Widgets are never deleted or retargeted. A widget bound to an archived identity shows **archived**; one bound to an identity without hardware shows **unassigned**. The resolution preview counts the affected widget measurements for an explicit dashboard edit.
- History lists archived identities with "(archived)". An identity that never had hardware has nothing to chart and is not offered.

## User interface

The Logical devices section of **Devices** provides:
- filters by room, provider, kind, assignment and active or archived status;
- creation badges, current hardware, measurements and previous devices;
- forms for create, edit, assign, replace, unassign (with an explicit end time) and archive (with confirmation), plus restore;
- an **Unassigned and new hardware** view.

The replacement form is the 7B form, extended with the release preview and confirmations. Viewers see the same lists without actions.

## Deployment

See the [README sequence](../../README.md#logical-devices-and-device-replacement): back up, migrate (017–019), restart, backfill, initialize (dry run first), validate. Provisioning starts covering new hardware only after initialization. To keep it off, set `LogicalDevices:AutoProvision=false` before restarting.

## Limitations

- Provisioning reacts within the sweep interval, not on the discovery event.
- Releasing hardware from an identity that also has other hardware history is allowed; the preview states it, and that identity is archived only if the user chooses and it has no other active device.
- Creating an identity for hardware that another identity uses requires releasing it through Assign or Replace on an existing identity first.
- Live behavior on the production inventory is unverified; only disposable databases and mocks were used.
