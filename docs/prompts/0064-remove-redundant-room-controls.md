# HomeAutomation — Remove redundant room assignment controls

Date: 2026-11-27

## Original prompt

## HomeAutomation — Remove redundant room assignment controls

Remove the inline room selection dropdown (`No room`) and `Save room` button from device cards on provider pages.

Requirements:

1. Remove these controls from the device cards for Daikin, eWeLink, Tuya and Growatt, wherever applicable.
2. Keep the existing room management functionality through the device's three-dot context menu (`Change room`) on the Devices page.
3. Preserve the visible `Assign here` button for devices not yet assigned to any property.
4. Do not remove or change property assignment functionality.
5. Do not change backend APIs or database relationships unless absolutely necessary.
6. Keep device cards compact and consistent with existing UI conventions.
7. Remove unused frontend state and handlers associated exclusively with the removed controls.
8. Add or update frontend regression tests.

## Constraints

- Do not modify or reset existing Configuration Vault credentials.
- Do not modify production or development database records as part of implementation or testing.
- Preserve existing OAuth flow behavior except for the requested safety improvements.
- Follow project architecture and UI conventions.
- Report any potentially destructive existing backend behavior before changing it.

Implement the change, add tests, and create a local commit. Do not push.

## Execution summary

- **Removed:** the inline room dropdown (`No room`) and **Save room** from Daikin, eWeLink and Tuya device cards. Growatt cards never had them. Also removed the per-card room state and mutations, the panels' room queries, the "Rooms could not be loaded" alerts and the cards' `rooms` props.
- **Kept:** **Assign here** on unassigned cards through a shared [AssignHere.tsx](../../src/frontend/home-automation-web/src/features/AssignHere.tsx). It calls the unchanged provider assign endpoints with no room and refreshes the property and management queries. eWeLink and Tuya show it only when the page allows managing devices, as before; Daikin shows it on every unassigned card, as before. Rooms are changed through **Change room…** on Devices, which is unchanged.
- **Backend:** no API, migration or database change. No destructive backend behavior was involved.
- **Tests:** the Daikin, eWeLink and Tuya regression tests check that only unassigned cards show Assign here, that it sends no room and refreshes the view, that no card has a room combobox or Save room, that rooms aren't requested, that a failure is shown on the card, and that viewers don't see Assign here.
- **Verification:** `npm.cmd run build` passed (only the size warning that already existed). `npm.cmd test` reported 113 passed in 12 files. Backend tests were not rerun because nothing in the backend changed.
- **Files:** `DaikinPanel.tsx`, `EwelinkPanel.tsx`, `TuyaPanel.tsx`, the new `AssignHere.tsx`, three updated test files, the [management doc](../architecture/16-property-room-device-management.md), the [frontend architecture](../architecture/03-frontend-architecture.md) and the [handoff](../AI_HANDOFF.md).
