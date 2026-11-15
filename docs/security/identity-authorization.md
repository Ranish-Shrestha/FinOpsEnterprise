# Identity and authorization

Authentication proves which Entra subject called the API. Authorization is evaluated separately, from assignments stored in SQL Server. A valid token alone grants no business access.

Decisions: [ADR 0002](../decisions/0002-identity-authorization.md) (model), [ADR 0003](../decisions/0003-hardening-and-demo-data.md) (demo data and HTTP hardening).

## Enforcement boundaries

| Boundary | Enforced by | Failure |
| --- | --- | --- |
| Signature, issuer, audience, lifetime | ASP.NET Core JWT bearer handler | 401 |
| API scope (`scp` contains `FinOps.Access`) | Fallback policy and every permission policy | 403 |
| Provisioned and active user | Lookup of `identity.UserProfiles` by immutable `oid`, `IsActive = 1` | 403 |
| Endpoint permission | `PermissionRequirement`: one policy per entry in `ApplicationPermissions.All` | 403 |
| Department visibility | Active dated membership, or the explicit `departments.read.all` permission | Filtered out |
| Request volume | Per-caller fixed window on `/api/*` (default 120 per 60 s) | 429 |
| Browser navigation | Convenience only. Never a security boundary | n/a |

Access is resolved **once per request**: the permission handler and the endpoint share one `CurrentUserAccess` result. Role and membership changes take effect on the next request; there is no authorization cache.

Membership dates use a half-open interval, `[StartsAtUtc, EndsAtUtc)`. Both the SQL filter in `UserAccessReader` and `DepartmentMembership.IsActiveAt` use it.

## Permission catalog

| Permission | Employee | Manager | FinanceOfficer | Administrator | Grants |
| --- | --- | --- | --- | --- | --- |
| `departments.read` | Yes | Yes | Yes | Yes | `GET /api/departments` |
| `departments.read.all` | | | Yes | Yes | All departments, regardless of membership |
| `user-roles.manage` | | | | Yes | Reserved for the admin feature (no endpoint yet) |
| `expenses.create` | Yes | Yes | Yes | | Create and manage your **own** expenses, only in departments where you are an active member |
| `expenses.read.department` | | Yes | | | Read expenses of departments where you are an active member |
| `expenses.read.all` | | | Yes | Yes | Read every expense |
| `expenses.approve` | | Yes | | | Approve or reject expenses of your **own departments**, never your own expense |
| `expenses.post` | | | Yes | | Post approved expenses, never your own |
| `audit.read` | | | | Yes | `GET /api/audit-events` |
| `budgets.read.department` | | Yes | | | Budgets and budget vs. actual for departments where you are an active member |
| `budgets.read.all` | | | Yes | Yes | Budgets and budget vs. actual for all departments |
| `budgets.manage` | | | Yes | | Create and change monthly budgets |
| `reports.read.department` | | Yes | | | Run the four financial reports for departments where you are an active member |
| `reports.read.all` | | | Yes | Yes | Run the four financial reports for all departments |
| `messaging.manage` | | | | Yes | View the outbox and replay dead-lettered messages |

The audit-activity report additionally requires `audit.read` (Administrator only).

**Separation of duties.** Managers approve, finance posts and sets budgets, and administrators manage access and read the audit trail but never approve, post or change money. The domain refuses any approval, rejection or posting by the expense's own requester, whatever the caller's permissions.

**Report and budget scope.** The API resolves the caller's departments from their active memberships and passes them to the reporting stored procedures as a typed JSON-array parameter.

- NULL means all departments, and is sent only for `*.read.all`. An empty array returns nothing.
- No client-supplied department filter ever reaches a procedure.
- The procedures validate their own parameters (THROW 50001–50006) in case they are called outside the API.
- Exports are audited (`report.exported`) and served with `Cache-Control: no-store`.
- SSRS is never the boundary; see [ADR 0004](../decisions/0004-report-access.md).

**Background processing.**

- Notifications are read and marked read only by their recipient. The user ID comes from the server, and anyone else's notification returns 404.
- Event payloads and notifications hold IDs and amounts, never descriptions or comments. The outbox API never returns payloads.
- The worker's own writes (`reimbursement.scheduled`) are audited with no actor and the originating request's correlation ID.
- Replaying a dead letter is audited (`message.replayed`). It cannot create a second reimbursement, because handlers are idempotent per message ID and the database allows one reimbursement per expense. It doesn't let an administrator move money either.

**Resource rules for expenses** (in `ExpenseAccessPolicy`, mirrored by the SQL list filter):

- *Visible* means you are the requester, or you have `expenses.read.all`, or you have `expenses.read.department` and an active membership in the expense's department.
- An invisible expense returns **404**, the same as one that doesn't exist.
- A visible expense you may not act on returns **403**.
- Every denied mutation is recorded in `audit.AuditEvents` with outcome `Denied`.

Roles, permissions and role-permission grants are **reference data** and ship in migrations. `PermissionCatalogTests` fails if the code catalog and the migrated rows disagree. To add a permission, add it to `ApplicationPermissions.All` and to `PermissionConfiguration`, then add a migration.

## Development personas

Created by `dotnet run --project src/FinOps.Api -- seed-demo-data` (Development only). Migrations never create user accounts.

| Persona | Mock object ID | Role | Memberships | Expected departments |
| --- | --- | --- | --- | --- |
| Demo Employee | `20000000-0000-0000-0000-000000000001` | Employee | Engineering (active) | Demo Engineering |
| Demo Manager | `20000000-0000-0000-0000-000000000002` | Manager | Finance (active), Operations (ended 2026-03-01) | Demo Finance |
| Demo Administrator | `20000000-0000-0000-0000-000000000003` | Administrator | none | All three |
| Demo Former Employee | `20000000-0000-0000-0000-000000000004` | Employee, **inactive** | Engineering (active) | 403 |
| Demo Finance Officer | `20000000-0000-0000-0000-000000000005` | FinanceOfficer | Finance (active) | All three |
| Demo Engineering Manager | `20000000-0000-0000-0000-000000000006` | Manager | Engineering (active) | Demo Engineering |

The seed also creates four sample expenses: an Employee draft, an Employee submitted expense, an Employee expense approved by the Engineering Manager and ready to post, and the Engineering Manager's own submitted expense, which shows the self-approval block.

In mock mode the `/login` page (and later the workspace top bar) has a persona picker. It sends `X-Development-User: <object ID>`. The API accepts that header only when `ASPNETCORE_ENVIRONMENT=Development` and `Security:UseDevelopmentMockAuthentication=true`. Enabling mock mode in any other environment fails at startup.

## Entra registration (real SSO)

For a free, step-by-step demo of Microsoft SSO with MFA and Google sign-in, see [sso-mfa-demo.md](sso-mfa-demo.md).

You need an Entra tenant in which you can register applications. The free tier is enough.

1. **API app registration.**
   - Under *Expose an API*, set the Application ID URI (for example `api://<api-client-id>`) and add a delegated scope named `FinOps.Access`.
   - Under *Manifest*, set `"requestedAccessTokenVersion": 2` (older manifests call it `accessTokenAcceptedVersion`). Without it Entra issues v1 tokens, the issuer does not match the v2 authority, and every call returns 401.
2. **SPA app registration.** Add a *Single-page application* platform with redirect URI `http://127.0.0.1:4200/`, then grant the API's `FinOps.Access` delegated permission. Do not create a client secret.
3. **API configuration.** Use user secrets or environment variables, never `appsettings.json`:

   ```powershell
   dotnet user-secrets set "Security:TenantId" "<tenant-id>" --project src/FinOps.Api
   dotnet user-secrets set "Security:Audience" "api://<api-client-id>" --project src/FinOps.Api
   dotnet user-secrets set "Security:UseDevelopmentMockAuthentication" "false" --project src/FinOps.Api
   ```

   With v2 tokens the `aud` claim is the API's client ID GUID, not the `api://` URI. If you get 401 with an audience error, set `Security:Audience` to the client ID instead.
4. **SPA configuration.** Edit `src/finops-web/public/config.json`, which is loaded at startup and overrides the defaults in `auth.config.ts`. Editing `auth.config.ts` alone has no effect while this file says `"mockMode": true`. These values are public identifiers, not secrets, but keep the change out of commits so the repository stays in mock mode:

   ```json
   {
     "apiBaseUrl": "",
     "mockMode": false,
     "tenantId": "<tenant-id GUID>",
     "spaClientId": "<SPA client-id GUID>",
     "apiScope": "api://<api-client-id>/FinOps.Access"
   }
   ```

   Keep `apiBaseUrl` empty locally, because the dev server proxies `/api`. The app refuses to start if `tenantId` or `spaClientId` is not a GUID, or if `apiScope` doesn't end with `/FinOps.Access`.
5. **Provision the user** by their Entra **object ID** (`oid`), not their email. The app's `/access-denied` page shows it after the first sign-in. Locally, run `dotnet run --project src/FinOps.Api -- provision-user --object-id <oid> --name "<name>" --email <email> --role <role> [--department "<department>"]`. In a deployed database, use [infra/sql/provision-user.sql](../../infra/sql/provision-user.sql). There is no admin UI yet.

## Deployed database access

In Azure, the API connects with a **user-assigned managed identity**. There is no SQL password: the server allows Microsoft Entra authentication only. The pipeline grants that identity only what the app uses (`DatabaseAdministration.GrantRuntimeAccessAsync`):

| Schema | Rights | Why |
| --- | --- | --- |
| `organization`, `identity` | SELECT | Users and roles are provisioned by an operator ([infra/sql/provision-user.sql](../../infra/sql/provision-user.sql)), never by the app |
| `finance`, `messaging` | SELECT, INSERT, UPDATE; EXECUTE (finance procedures) | The app never deletes |
| `audit` | SELECT, INSERT; EXECUTE | The running app cannot alter or remove audit history |

There is no DELETE, no DDL and no `db_owner`. `DeploymentTests` run the full business flow and the worker as a login with exactly these grants. They also check that DELETE, audit rewrites, identity writes and DDL are refused.

## Rules

- Never log tokens, `Authorization` headers or client secrets. Do not enable EF sensitive-data logging.
- Token role claims, email addresses and client-submitted department IDs are never authorization inputs.
- Before any deployment: disable mock authentication, use HTTPS with HSTS (enabled outside Development), set exact tenant and audience values, provision users deliberately, and revisit SPA token storage and CSP (see ADR 0003).
