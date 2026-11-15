# ADR 0008: Client-side routing and identity pages

Status: accepted (2026-11-15). Builds on [ADR 0002](0002-identity-authorization.md), which it does not change.

## Context

Until now the Angular app was a single page. `@angular/router` was not installed, and every feature panel (overview, notifications, expenses, budgets, reports) was stacked in `app.html`. That kept the early phases small, but:

- nothing could be linked or bookmarked, such as an expense to approve or the reports page;
- browser back and forward did nothing, and a refresh lost the open tab or expense;
- sign-in was a button on the workspace itself, with no place for the usual account screens (sign in, sign up, forgot password, MFA, no access, signed out);
- every feature's code loaded up front.

## Decision

1. **Use the Angular router, with two layouts.**
   - `AuthLayout` is a centered card for `/login`, `/signup`, `/forgot-password`, `/mfa`, `/access-denied`, `/signed-out` and the 404 page.
   - `ShellLayout` holds the sidebar, top bar and user menu for `/dashboard`, `/expenses[/:id]`, `/budgets`, `/reports`, `/notifications` and `/admin/outbox`.
   - Every page is lazy loaded.
2. **Guards only steer navigation.**
   - `authGuard` sends a signed-out user to `/login?returnUrl=…`, and an account without a FinOps profile to `/access-denied`.
   - `permissionGuard` applies the same permission lists the sidebar uses (`core/permissions.ts`).
   - The API still authorizes every request; a guard is a convenience, never a control.
3. **Entra stays the only identity provider, so the account pages don't handle credentials.**
   - `/signup` is *Request access*: users are provisioned by an administrator (ADR 0002), so the page explains how to get access.
   - `/forgot-password` points to Entra self-service password reset.
   - `/mfa` explains that Entra Conditional Access enforces MFA, and links to Microsoft security info.
   - We store no passwords, send no reset emails and keep no MFA secrets.
4. **The SSO redirect URI stays the site root (`/`).** An app initializer finishes the authorization code exchange before the router's first navigation, then replaces the address with the saved return URL. The Entra app registration doesn't change. The return URL must be a same-origin path, so `//host` and `/\host` are rejected (`safeReturnUrl`).
5. **Mock mode signs in explicitly.** In mock mode, the user picks a persona on `/login` and clicks *Continue*. The top-bar picker can switch persona later. Signing out now really signs out.
6. **The resolved user and departments are bound to the feature components' existing `user` and `departments` inputs** (`withComponentInputBinding`), so the components and their tests are unchanged. The Expenses panel mirrors its tab (`?view=`) and the open expense (`/expenses/:id`) into the URL.

## Consequences

- Deep links, back and forward, and refresh work. `staticwebapp.config.json` already rewrote unknown paths to `index.html`.
- Each page is its own chunk, so the initial bundle carries no feature code.
- E2E journeys now sign in on `/login` and navigate with the sidebar.
- If self-service sign-up is wanted later, it means Entra External ID (CIAM) user flows, not local accounts. The pages here would then link to that flow.
