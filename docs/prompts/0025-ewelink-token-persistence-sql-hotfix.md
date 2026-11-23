# Hotfix — eWeLink OAuth Token Persistence SQL Error

Date: 2026-11-23

## Original prompt

# Hotfix — eWeLink OAuth Token Persistence SQL Error

The automatic eWeLink OAuth callback now reaches token persistence but fails with:



MySqlConnector.MySqlException:\
Column 'owner_id' in field list is ambiguous



Stack trace:

- EwelinkStore.SaveTokensAsync — EwelinkStore.cs:42
- EwelinkService.ConnectAsync — EwelinkService.cs:130
- EwelinkEndpoints.cs:63

This is similar to the previously fixed Tuya upsert ambiguity.



Tasks:

1. Inspect the SQL in EwelinkStore.SaveTokensAsync and migration 004.
2. Fix the ambiguous owner_id reference using valid MySQL 8.0 syntax.
3. Preserve encrypted token storage, ownership checks, and existing upsert semantics.
4. Inspect other EwelinkStore SQL statements for similar ambiguity.
5. Add a focused regression test against isolated MySQL 8.0.46, following the Tuya hotfix test pattern.
6. Verify token persistence and readback without using real credentials.
7. Run relevant builds and tests.
8. Do not modify OAuth security, API signatures, database schema, or other integrations.
9. Do not make live OAuth requests or physical device commands.
10. Commit locally without pushing.

Keep the change minimal and report the commit hash and verification results.

## Execution summary

Qualified the existing-row owner, token envelope, and status in the eWeLink account upsert. The device upsert already qualifies the incoming snapshot. The remaining queries use single-table scopes and have no equivalent ambiguous reference.

The regression reproduced the original error on disposable MySQL 8.0.46 using the unchanged foundation and eWeLink migrations. After the fix it verified encrypted token insert and readback, rotation, owner-only disconnect and reconnect, and protection against another owner changing the envelope or status. The .NET Release build passed with zero warnings and errors. The backend suite recorded 108 passes, including the eWeLink MySQL regression, and three opt-in Tuya MySQL skips. No live OAuth or device requests were made.

See the [eWeLink store](../../src/backend/HomeAutomation.Infrastructure/Ewelink/EwelinkStore.cs) and [MySQL regression](../../tests/HomeAutomation.UnitTests/EwelinkMySqlTests.cs).
