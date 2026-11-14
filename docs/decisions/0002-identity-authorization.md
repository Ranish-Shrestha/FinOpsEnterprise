# ADR 0002: Entra authentication and application authorization

Status: accepted (2026-11-06). Amended by [ADR 0003](0003-hardening-and-demo-data.md): demo users moved out of migrations, and access is resolved once per request. Operational details: [security/identity-authorization.md](../security/identity-authorization.md).

## Decision

Use Entra v2 OIDC/OAuth authorization code with PKCE for the Angular public client and JWT bearer validation in the API. The token's immutable `oid` identifies a provisioned `UserProfile`; roles, permissions, active memberships, and resource access come only from SQL Server.

The API uses a fallback policy that denies anonymous access. Permission requirements query application-owned authorization data, and department lists are filtered before projection. Browser filters, token role claims, email addresses, and client-submitted department IDs are not authorization authority.

A header-based mock identity exists only in Development. Enabling it in another environment fails startup. Its object IDs refer to synthetic provisioned records, so it exercises the same authorization query as Entra users. (Since ADR 0003, those records come from the `seed-demo-data` command, not from migrations.)

## Consequences

- Role or membership changes take effect on the next request; Phase 2 intentionally has no authorization cache to invalidate.
- Users are explicitly provisioned rather than silently created from a token.
- The SPA stores an access token in session storage for this portfolio phase. A production threat model should revisit token storage and CSP before deployment.
- Phase 3 resource handlers must combine permission, ownership, department, workflow state, and self-approval rules.
