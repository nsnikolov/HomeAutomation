# HomeAutomation — Fix Initial Property Ownership

Date: 2026-11-24

## Original prompt

# HomeAutomation — Fix Initial Property Ownership

The current authenticated user is the only member of property 1 (Countryside House), but has the Viewer role.



The configurable dashboard correctly requires Owner, Admin or Operator permissions for editing.



Investigate how the initial property and its membership were created.



Requirements:

1. Verify that the current user is the sole member of property 1.
2. If verified, provide a safe, one-time administrative procedure to promote that membership to Owner.
3. Do not weaken authorization or change roles globally.
4. Ensure newly created properties assign Owner to their creator.
5. Add focused regression tests.
6. Preserve all existing properties, devices, telemetry and dashboard configuration.
7. Run relevant tests and commit locally without pushing.

Do not silently modify live database membership. Explain the exact administrative action required.

## Execution summary

A read-only check confirmed that property 1 is the seeded Countryside House and has one Viewer membership. Migration 002 created the property, and account bootstrap granted Viewer separately.

Added an explicit `--promote-initial-owner` command that requires the sole member's confirmed local account and limits the change to the seeded property. Added `--create-property` to create a property and its creator's Owner membership atomically. The live database membership was not changed. No migration or changes to dashboard authorization were needed.

The Release build passed without warnings. The backend suite passed 163 tests, with eight unrelated opt-in tests skipped. Isolated MySQL 8.0.46 verified wrong-account and multiple-member rejection, transaction rollback, dashboard preservation, and both administrative commands.

- [One-time promotion and property creation](../../README.md#assign-the-initial-owner-of-countryside-house)
- [Property administration store](../../src/backend/HomeAutomation.Infrastructure/MySql/PropertyAdministration.cs)
