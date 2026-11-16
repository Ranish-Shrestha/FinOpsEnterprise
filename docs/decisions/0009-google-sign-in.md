# ADR 0009: Optional direct Google sign-in

Status: accepted (2026-11-15). Adds to [ADR 0002](0002-identity-authorization.md) and [ADR 0008](0008-client-routing.md); Entra remains the primary identity provider.

## Context

A real SSO demo with Entra needs an Entra tenant. For someone who only has a personal Microsoft or Gmail account, getting a tenant means the Azure free account sign-up, which asks for a card. A Google OAuth client is free and needs no card, so Google sign-in is the cheapest way to show real single sign-on locally.

## Decision

1. **Google is an optional, second identity provider, turned on by configuration.**
   - It's on when the API has `Security:GoogleClientId` and `config.json` has `googleClientId`.
   - Entra, Google or both can be configured. Mock mode still replaces both, and only in Development.
2. **The browser gets a Google ID token directly.**
   - It uses Google's OIDC authorization endpoint with `response_type=id_token`, a one-time `state` and a `nonce`, and the same redirect URI (`/`) as Entra.
   - Google requires a client secret for the authorization code flow even with PKCE. A single-page app can't keep a secret, so the implicit ID-token response is the secret-free option.
   - The browser checks `state` and `nonce`, so a token pasted into the URL is refused. It doesn't check the signature.
3. **The API validates the ID token as a bearer token.**
   - Google's keys come from `https://accounts.google.com`. The issuer, the audience (this app's Google client ID), the lifetime and the signature are all checked.
   - With both providers configured, a policy scheme picks the validator from the token's unverified `iss`. The chosen validator then checks everything, so a forged `iss` only selects a validator that rejects the token.
4. **Google accounts get a stable application object ID.**
   - The ID is the first 16 bytes of `SHA-256("https://accounts.google.com|" + sub)`, stamped as an RFC 9562 version 8 GUID. Google's `sub` is the account's permanent ID; we never use the email.
   - It's added as the `oid` claim after validation, so provisioning, permission checks, rate limiting and auditing work unchanged. The `EntraObjectId` column holds it.
   - `/access-denied` shows it from `GET /api/me/sign-in` (`accountId`), and `provision-user` provisions it like any Entra object ID.
5. **The audience stands in for the API scope.** A Google ID token has no `scp`. The validator adds `scp = FinOps.Access` only to a token whose audience is this app's own Google client ID, which means it was issued for FinOps.
6. **Authorization doesn't change.** Any Google account can sign in, but without a provisioned, active profile every application endpoint answers 403.

## Consequences

- **MFA isn't reported.** Google ID tokens have no `amr`, so the API can't tell whether Google asked for 2-Step Verification. The dashboard shows *Not reported*; the 2-Step prompt itself is the demo's evidence. Enforcing MFA for Google accounts needs Google Workspace or Entra federation, as in [the demo guide](../security/sso-mfa-demo.md).
- **Sessions last about an hour.** There's no refresh token: when the ID token expires the API answers 401, and the user signs in again. Google usually signs them straight back in.
- **Sign-out is local only.** Signing out clears this app's token, but the browser stays signed in to Google.
- **The column name is now inaccurate.** For Google users, `EntraObjectId` holds a derived ID. Renaming it to something like `ExternalObjectId` is left for a later migration.
