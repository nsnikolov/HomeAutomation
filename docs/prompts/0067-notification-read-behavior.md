# HomeAutomation — Fix Notification Read Behavior

Date: 2026-11-27

## Original prompt

# HomeAutomation — Fix Notification Read Behavior

Investigate and improve notification read/unread behavior in the notification bell, Alerts page and incident details.

## Current issue

Opening the notification bell and viewing the test notification leaves it Unread and the counter at 1.

This is acceptable when merely opening the notification list.

However, selecting an individual notification should mark it as read automatically.

## Required behavior

1. Opening the notification bell must not automatically mark notifications as read.
2. Clicking an individual notification must:
   - Mark that notification as read for the current user.
   - Navigate to the associated incident or notification details.
   - Update the unread counter immediately after successful persistence.
   - Update the notification's visual state.
3. Opening a linked incident directly may mark the relevant notification as read when its identity can be determined unambiguously.
4. Keep Mark all read functionality.
5. Preserve separate read, acknowledgment and resolution states.
6. Marking a notification as read must never acknowledge or resolve an incident.
7. Read state must be stored per user.
8. Changes must be reflected across open browser tabs through existing SignalR or state synchronization mechanisms.
9. Handle API failures without falsely reporting successful persistence.
10. Prevent unnecessary duplicate API requests.
11. Make unread notifications visually distinguishable.
12. Ensure keyboard accessibility and mobile-responsive behavior.

## Testing

Add regression tests covering:
- Opening bell without marking notifications read.
- Clicking one unread notification.
- Immediate unread counter update.
- Persistence after page reload.
- Independent read state between users.
- Mark all read.
- API failures.
- Multiple browser tabs.
- Incident acknowledgment remaining unchanged.
- Incident resolution remaining unchanged.

Follow existing API and frontend architecture.

## Constraints

- Do not modify or reset existing Configuration Vault credentials.
- Do not modify production or development database records as part of implementation or testing.
- Preserve existing OAuth flow behavior except for the requested safety improvements.
- Follow project architecture and UI conventions.
- Report any potentially destructive existing backend behavior before changing it.

Implement the change, add tests, and create a local commit. Do not push.

## Execution summary

**Findings.** The backend already met the storage and synchronization requirements, so it is unchanged:
- **Per user:** read state is stored per recipient (`notification_recipients.read_at_utc`).
- **Scope:** `POST /notifications/read` accepts explicit IDs or all, limited to the caller's own rows. It only sets `read_at_utc`; it never touches incidents, acknowledgments or resolution.
- **Other tabs:** after a change, it sends `notificationsChanged` to every connection of that user.

The gaps were in the frontend:
- bell items were plain links that marked nothing;
- nothing updated until a refetch;
- failures were silent;
- nothing prevented repeated requests;
- history titles weren't clickable.

**Race fixed.** When a read finished while the first summary request was still in flight, `invalidateQueries` joined that older request, and the stale unread count stayed. A system notification opening the page could trigger this, and so could a `notificationsChanged` event that arrived during a fetch. `refreshNotifications` now cancels in-flight notification reads, applies the confirmed change, then refetches. The live handler uses the same helper.

**Changes (frontend only).**
- **`NotificationCenter.tsx`:** `useNotificationReads` marks one notification or all of them.
  - **Duplicates:** an already-read notification sends nothing, and IDs already in flight in this tab are skipped.
  - **Display:** lists and counters change only after the server confirms.
  - **Failures:** an error is shown, and the notification stays unread.
- **Bell:**
  - opening it reads nothing;
  - each notification is one full-row link that opens its incident and marks it read;
  - unread rows show an accent border, bold title and an "Unread" label;
  - a read failure appears as a dismissible alert;
  - rows are at least 44 px tall (48 px on touch screens);
  - Mark all read is kept.
- **`AlertsPage.tsx`:**
  - `?notification=<id>` marks exactly that notification read once, then is removed from the URL. A system notification click now includes it.
  - A plain incident link marks nothing, because an incident can have several notifications.
  - History titles link to their incident and mark the notification read; per-item Mark read has an accessible name.
  - Opening an incident link switches to the Incidents tab.
- **Other files:** `shared/alerts.ts` adds `refreshNotifications`; `shared/live.ts` uses it for `notificationsChanged`; `styles.css` adds the unread, focus and error styles.

**Tests.**
- **Frontend:** `features/alerts/NotificationReads.test.tsx` has 10 tests. They use an in-memory per-user fake server that broadcasts `notificationsChanged`, plus a mocked SignalR connection for two tabs. They cover:
  - opening the bell without reads;
  - clicking one notification, with navigation and a counter confirmed before any refetch;
  - an already-read notification, and duplicate clicks sending one request;
  - failures for single and all reads;
  - Mark all read with a reload, a single read with a reload, and per-user state;
  - two tabs;
  - a direct link with and without `notification`;
  - history links;
  - keyboard access;
  - acknowledgment and resolution never requested.
- **Backend:** `AlertMySqlTests.ReadingOneNotificationPersistsForItsReaderOnlyAndNeverAcknowledgesOrResolvesTheIncident`.

**Results.**
- **Backend:** Release build with zero warnings. With disposable MySQL 8.0.46 fixtures, 357 passed and 11 skipped, twice in a row; without fixtures, 309 passed and 59 skipped.
- **Frontend:** the production build passed (main chunk 524 kB; the size warning predates this). Vitest reported 145 passes.

**Not touched.** No live or development database, vault credential, OAuth flow, running backend or provider was touched. Nothing was pushed.

**Destructive behavior.** No potentially destructive existing behavior was found or changed.
