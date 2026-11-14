# ADR 0003: Demo identities out of migrations; HTTP hardening

Status: accepted (2026-11-14).

## Context

Phase 2 seeded three demo users, including an Administrator, through EF `HasData`. Migrations run in every environment, so any database migrated with them, including a future Azure SQL database, would contain a pre-provisioned administrator. It was not exploitable, because Entra issues the real object IDs. It still contradicted the principle that users are provisioned deliberately.

A review also found:

- Each request loaded the caller's access twice (8 queries).
- Each permission needed a hand-written policy.
- The mock scheme hard-coded the scope name.
- Basic HTTP hardening was missing.
- Several spec §9 authorization cases were untested.

## Decision

1. **Reference data and demo data are separate.**
   - Roles, permissions and role-permission grants stay in migrations. They are part of the schema contract.
   - Users, role assignments and memberships are removed from the model by migration `RemoveDemoIdentitySeedData`.
   - `DemoDataSeeder` inserts them only when a developer runs `dotnet run --project src/FinOps.Api -- seed-demo-data`. The command refuses to run outside Development and is idempotent.
   - Demo departments remain migration-seeded for now, because they are harmless organizational reference rows. Revisit this when a department admin feature exists.
2. **One access lookup per request.** `CurrentUserAccess` is scoped and caches its task. The permission handler and endpoints share it, and it uses `HttpContext.RequestAborted`.
3. **Policies are generated** from `ApplicationPermissions.All`. A test pins the catalog to the migrated permission rows.
4. **HTTP hardening:**
   - Inbound-safe correlation IDs in the log scope.
   - Security headers and `no-store` on API responses.
   - HSTS outside Development.
   - A per-caller fixed-window rate limit on `/api/*`, returning 429.
5. **The SPA keeps the hand-rolled PKCE client for now.** It gains scoped sign-out, single-use state and verifier values, 401 handling and a persona picker.

## Consequences

- **Existing local databases:** `dotnet ef database update` deletes the three old demo users, together with their roles and memberships. Run `seed-demo-data` afterwards. Rolling back below this migration after re-seeding will fail on duplicate keys; drop the database instead.
- **New databases start with no accounts.** Real users must be provisioned explicitly (see [identity-authorization.md](../security/identity-authorization.md)).
- **Database tests** run the same migrate-then-seed sequence as a developer.

## Deferred: MSAL

The hand-rolled client requests `openid` but does not validate an ID token, and it has no silent renewal. When the access token expires, the user signs in again. That is acceptable for a local portfolio phase and keeps the dependency surface small.

Before any deployed environment, replace it with `@azure/msal-browser` or `@azure/msal-angular`. That brings ID-token and nonce validation, silent renewal and cache handling. Revisit `sessionStorage` token storage and add a SPA CSP at the same time.
