# Property, room and device management

Settings now manages properties and rooms, and Devices manages custom names, optional rooms and moves between properties. Migration 020 adds the columns and the `management_audit` table. Nothing deletes devices, telemetry or provider credentials.

## Where default data came from

| Source | What it creates | Status |
| --- | --- | --- |
| Migration 002 | Countryside House (`countryside`), rooms `plant`, `living`, `utility`, the `solar` installation and the `simulator` connection. | Runs once. The migration ledger prevents reseeding. |
| `--migrate --bootstrap-user` | Viewer membership in `countryside` for the bootstrapped account. | Unchanged. |
| Growatt ingest | The `growatt` installation and connection for the property whose site key the Growatt configuration names. | Unchanged; only that property. |
| `--create-property`, `PropertyAdministration.CreateAsync` | The property and its Owner membership. | Unchanged. |
| **Add property** (new) | The property and the creator's Owner membership. | No rooms, devices, installations, simulator data or provider profiles. |

The vault semantics are unchanged. Daikin, eWeLink and Tuya profiles are global (scope 0), Growatt profiles are per property, and a property never inherits a global profile.

## Roles

| Operation | Who |
| --- | --- |
| List properties | Any member; deleted properties are hidden. |
| Create a property | The installation administrator (`ConfigurationVault:AdministratorUserId`). |
| Edit name, description, time zone; room CRUD; rename a device; change its room | Owner or Admin of the property. |
| Move a device | Owner or Admin in both the source and the destination. |
| Delete a property | Owner. |

The API enforces these rules. The UI hides actions the server would refuse, but that is only a convenience. Non-members get 404, and members without the role get 403. Integration tests cover the antiforgery requirement and the membership filter. MySQL tests cover each role check.

## Properties

- **Create** — `POST /api/v1/property-management/properties` with `name`, optional `description`, `timeZone` (default `Europe/Sofia`, validated as an IANA zone) and a client `requestId`. The site key is a slug plus 8 hex characters. A repeated request id returns the first property instead of creating a second one.
- **Edit** — `PUT /properties/{id}/details`. Devices, history and provider settings are unchanged.
- **Delete** — `GET /properties/{id}/deletion-impact` reports rooms, current devices, installations, devices moved away (historical rows), members, vault profiles and dashboard groups. `POST /properties/{id}/delete` takes the exact property name.
  - Only **current** devices block deletion. Those are non-retired device rows plus provider rows with no active bound device.
  - Deletion is a soft delete (`properties.deleted_at_utc`). Every membership check excludes deleted properties, so the property disappears for all members at once.
  - Rooms, memberships, retired devices, telemetry, rollups, logical history, dashboards and vault profiles stay in the database. Vault credentials are never deleted silently.
  - The property row lock (`FOR UPDATE`) serializes deletion with assignments and transfers. Provider assignment statements also check `deleted_at_utc IS NULL` inside the `UPDATE`. An assignment that waited on the lock therefore cannot land in a property deleted meanwhile.
- If the deleted property is open in the browser, the UI returns to the property list.

## Rooms

Rooms are optional. `GET/POST /properties/{id}/room-management`, `PUT/DELETE /room-management/{roomId}`. Names are unique per property, ignoring case and surrounding spaces. A new property shows "No rooms yet. Add your first room."

Deleting a room clears every reference in one transaction, then deletes the row. The references live in `devices`, `logical_devices`, `daikin_devices`, `ewelink_devices` and `tuya_devices`. Devices stay in the property with bindings, state, telemetry, alerts and audit. This needs **`DELETE` on `rooms`** for the runtime account. Without it the API returns 503 with that explanation and rolls the clearing back.

`TelemetryWriter` copies the provider room into `devices.room_id` on each sync, so room changes are written to both the provider row and the device row.

## One-time room-assignment clearing

```powershell
# Dry run: counts per property and table. Writes nothing.
dotnet run --project src/backend/HomeAutomation.Api -- --clear-room-assignments
# After a verified backup:
dotnet run --project src/backend/HomeAutomation.Api -- --clear-room-assignments --execute --confirm-room-assignment-clearing
```

It sets `room_id` to NULL in the five tables for every property, in one transaction, with one `roomAssignmentsCleared` audit row per property. Rooms, devices, bindings, telemetry, alerts and vault profiles are untouched. A rerun reports zero. `--execute` without the confirmation flag is refused. The runtime account is sufficient. It has **not** been run against any development or production database.

## Moving a device between properties

Telemetry is property-scoped. `devices`, `device_endpoints` and `telemetry_series` carry `property_id` with composite foreign keys, and samples hang off series. A move therefore leaves history where it was recorded and opens a new identity in the destination.

1. Both property rows are locked in id order. The device must be current and bound to Daikin, eWeLink or Tuya.
   - **Refused:** Growatt (follows the site configuration), simulator and unknown providers, and a device with an open Daikin command (dispatched, awaiting confirmation, uncertain or pending) or an unresolved eWeLink relay command.
2. The source device row is retired (`retired_at_utc`). Its endpoints, series, samples, rollups and audit stay with the source.
3. The destination gets a device row with the same inventory key. If the device was there before, its old row is reactivated.
   - **Copied:** display name, endpoints, capabilities, provider binding and endpoint bindings.
4. The provider row moves: `property_id` = destination, `room_id` = NULL. eWeLink `relay_write_enabled` is reset, so relay controls must be enabled again deliberately.
5. Open logical assignments in the source close at the move instant. Each gets a `logical_device_audit` `unassigned` row. No telemetry is rewritten.
6. `deviceTransferredOut` and `deviceTransferredIn` audit rows link the two properties and device rows.

The next provider sync writes to the destination identity. Readers exclude retired rows, so the source no longer lists the device. Its history stays available in the source's History and logical history, and the destination shows only new readings. `GET /devices/{id}/transfer-impact?destination=` previews this, and `POST /devices/{id}/transfer` performs it. Concurrent moves of one device let exactly one succeed. `GET /transferable-devices` lists current devices in the user's other Owner/Admin properties for **Move to this property**.

## Custom names and device audit

`devices.display_name` overrides the provider name everywhere a device is shown: monitoring, history, dashboard catalog and logical devices. Provider sync updates only `name`, so the custom name survives synchronization and moves with the device. An empty name clears it. `GET /devices/{id}/audit` returns assignment, room, name and move events for the device page. Actors are recorded but not shown.

## Frontend

- **Settings** shows **Properties** (create, edit, delete with impact review and typed name) and **Rooms**. Both pages and the first-property form are lazy chunks.
- **Devices** adds a three-dot menu per device with **Rename…**, **Change room…** and **Move to another property…**. A **Devices in your other properties** section offers **Move to this property…**.
- **Provider pages:** device cards carry no room controls. **Assign here** remains on unassigned provider devices for users the provider page lets manage devices. It assigns the device to the property with no room, using the unchanged provider assign endpoints. A device's room is set or changed only through **Change room…** in its menu on **Devices**. Growatt cards never had room controls. Room choice is optional everywhere.
- **Daikin ONECTA:** **Refresh discovery** stays visible. **Reconnect account…** (plain confirmation, then the unchanged OAuth flow) and **Disconnect account…** sit in an account menu. Disconnect requires typing `DISCONNECT`.
  - Disconnect behaves as before: it clears only the stored ONECTA token envelope and sets the account to `disconnected`. Devices, rooms, history, alerts and vault profiles stay. Reconnecting requires Daikin sign-in.
- `ConfirmDialog` is the shared modal: focus trap, Escape, focus restore, typed confirmation, blocked state, errors kept in the dialog, and one submission at a time. `ActionMenu` is the shared three-dot menu with keyboard support.
- After each operation the affected property queries, the property list and the management view are invalidated, so no reload is needed.

## Deployment

Migration 020 is **required**: base readiness checks it, and the API reports not ready until it is applied. Apply it with the migration account (it needs `CREATE`, `ALTER`, `REFERENCES` as for earlier migrations). Then grant the runtime account `DELETE` on `rooms`:

```sql
GRANT DELETE ON homeautomation.rooms TO 'home_runtime'@'127.0.0.1';
```

The Docker runtime user already has schema-wide `DELETE`. No backfill is needed. Existing devices keep their provider names until someone renames them.

## Limits

- Deleted properties can be restored only by a database administrator (`deleted_at_utc = NULL`); there is no UI for it.
- Moving Growatt hardware still needs a site configuration change.
- A moved device's logical identities stay in the source. Create or assign a logical device in the destination.
- The audit lists the latest 100 events per device.
