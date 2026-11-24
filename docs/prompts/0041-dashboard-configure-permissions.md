# Investigate missing Configure dashboard action

Date: 2026-11-24

## Original prompt

Investigate why the new configurable monitoring Dashboard shows "No monitoring widgets yet" but does not display the "Configure dashboard" action.



Current user is authenticated and viewing property 1 (Countryside House).



Milestone 5A was implemented in commit bdbbb70 and migration 011 has been applied.



Tasks:

1. Inspect the current user's property role and effective permissions.
2. Check the backend authorization and frontend visibility logic for dashboard configuration.
3. Ensure Owner, Admin and Operator users can access "Configure dashboard".
4. Viewer users must remain read-only.
5. If the current user is authorized, fix the missing configuration action.
6. Add focused regression tests.

Do not weaken authorization or change unrelated functionality.



Run relevant tests and commit locally without pushing.

## Execution summary

A read-only audit of local MySQL confirmed migration 011 and one membership for property 1. That membership has the Viewer role, and the dashboard store returned `canEdit: false` and zero groups. The frontend uses that permission to decide whether to render Configure dashboard. Production authorization and membership data needed no changes.

The isolated MySQL regression now checks edit capability, catalog access, and saves for Owner, Admin, and Operator, along with Viewer and cross-property restrictions. An empty-dashboard frontend regression verifies that a Viewer sees the empty state without the configuration action. Two focused backend tests and six frontend tests passed.

- [Dashboard authorization](../../src/backend/HomeAutomation.Infrastructure/MySql/MonitoringDashboardStore.cs)
- [Dashboard setup and property roles](../../README.md#configurable-monitoring-dashboard)
