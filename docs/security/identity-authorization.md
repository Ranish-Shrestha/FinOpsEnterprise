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

**Separation of duties.** Managers approve, finance posts, and administrators manage access and read the audit trail but never approve or post money. The domain refuses any approval, rejection or posting by the expense's own requester, whatever the caller's permissions.

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

In mock mode the Angular shell has a persona picker. It sends `X-Development-User: <object ID>`. The API accepts that header only when `ASPNETCORE_ENVIRONMENT=Development` and `Security:UseDevelopmentMockAuthentication=true`. Enabling mock mode in any other environment fails at startup.

## Entra registration (real SSO)

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
4. **SPA configuration.** In `src/finops-web/src/app/auth.config.ts`, set `tenantId`, `spaClientId`, `apiScope` and `mockMode: false`. These values are public identifiers, not secrets.
5. **Provision the user.** Insert a row in `identity.UserProfiles` with the user's Entra **object ID** (`oid`), not their email. Assign a role in `identity.UserRoles` and, if needed, a membership in `identity.DepartmentMemberships`. There is no admin UI yet.

## Rules

- Never log tokens, `Authorization` headers or client secrets. Do not enable EF sensitive-data logging.
- Token role claims, email addresses and client-submitted department IDs are never authorization inputs.
- Before any deployment: disable mock authentication, use HTTPS with HSTS (enabled outside Development), set exact tenant and audience values, provision users deliberately, and revisit SPA token storage and CSP (see ADR 0003).
