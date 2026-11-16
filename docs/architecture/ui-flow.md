# UI flow

How the Angular app starts, signs in, and decides what each user sees. The app uses the Angular router with two layouts: a centered **auth layout** for the sign-in and account-help pages, and a **shell layout** (sidebar, top bar, user menu) for the workspace pages. Which pages and sections a caller sees depends on the permissions the API returns from `GET /api/me`. See [ADR 0008](../decisions/0008-client-routing.md).

The UI never decides access on its own. Hiding a button is a convenience: the API checks every request again, and returns 403 for anything the caller may not do.

Source: [main.ts](../../src/finops-web/src/main.ts), [app.config.ts](../../src/finops-web/src/app/app.config.ts), [app.routes.ts](../../src/finops-web/src/app/app.routes.ts), [core/guards.ts](../../src/finops-web/src/app/core/guards.ts), [auth.service.ts](../../src/finops-web/src/app/auth.service.ts) and the feature folders under [src/finops-web/src/app](../../src/finops-web/src/app).

## Pages and routes

| Route | Layout | Guard | Page |
| --- | --- | --- | --- |
| `/login` | Auth | signed out only | *Sign in with Microsoft* (Entra SSO). In mock mode: persona picker and *Continue* |
| `/signup` | Auth | signed out only | *Request access*: there is no self-service sign-up, because an administrator provisions accounts |
| `/forgot-password` | Auth | none | Link to Entra self-service password reset. We never store passwords |
| `/mfa` | Auth | none | MFA is enforced by Entra Conditional Access; link to Microsoft security info |
| `/access-denied` | Auth | none | Signed in, but no active FinOps profile (`/api/me` 403); shows the Object ID to send to an administrator |
| `/signed-out` | Auth | none | Confirmation, and *Sign in again* |
| `/dashboard` | Shell | signed in | The user's work by role: counts, approvals waiting, recent expenses, this month's budget, unread updates; system status on one line |
| `/expenses`, `/expenses/:id` | Shell | signed in | Expenses. The tab is `?view=mine\|queue\|toPost\|all`; `:id` opens the detail pane |
| `/budgets` | Shell | `budgets.read.department` or `.all` | Budget vs. actual |
| `/reports` | Shell | `reports.read.department`, `.all` or `audit.read` | Reports |
| `/notifications` | Shell | signed in | Notifications |
| `/admin/outbox` | Shell | `messaging.manage` | Outbox status, dead letters, Replay |
| anything else | Auth | none | Page not found |

```mermaid
flowchart TD
  Nav(["Navigate to a workspace page"]) --> Signed{"Signed in?"}
  Signed -- "no" --> Login["/login?returnUrl=the page"]
  Signed -- "yes" --> Me{"GET /api/me"}
  Me -- "403 (API up)" --> Denied["/access-denied"]
  Me -- "profile" --> Perm{"Page permission?"}
  Me -- "API down" --> Dash
  Perm -- "no" --> Dash["/dashboard"]
  Perm -- "yes" --> Page["The page"]
  Login -- "sign in" --> Back["Back to returnUrl<br/>(same-origin paths only)"] --> Nav
```

The guards only choose a page. The sidebar uses the same permission lists ([core/permissions.ts](../../src/finops-web/src/app/core/permissions.ts)), and the API still authorizes every request. Switching persona in mock mode re-runs the guards, so a page the new persona may not use redirects to the dashboard.

## 1. Start-up and sign-in

```mermaid
flowchart TD
  Open(["Open the app URL"]) --> Cfg{"GET /config.json"}
  Cfg -- "404 (local dev)" --> Defaults["Keep the defaults in auth.config.ts<br/>(mock mode)"]
  Cfg -- "200" --> Valid{"Valid?<br/>https API origin,<br/>GUID tenant and client IDs"}
  Cfg -- "other error" --> Fail["Start-up stops<br/>(error in console)"]
  Valid -- "no" --> Fail
  Valid -- "yes" --> Apply["Apply the runtime settings"]
  Defaults --> Boot["Bootstrap Angular"]
  Apply --> Boot
  Boot --> Mode{"Mock mode?"}

  Mode -- "yes (Development)" --> Persona["/login with the<br/>Development persona picker"]
  Persona -- "choose a persona, Continue" --> Load

  Mode -- "no (Entra)" --> HasToken{"Unexpired token<br/>in this tab?"}
  HasToken -- "yes" --> Load
  HasToken -- "no" --> Code{"?code= in the URL?<br/>(returning from Entra)"}
  Code -- "no" --> SignIn["/login with<br/>Sign in with Microsoft"]
  SignIn -- "click Sign in" --> Authorize["Redirect to Entra authorize<br/>(PKCE S256 challenge, state)"]
  Authorize -- "user signs in" --> Back["Entra redirects back<br/>with ?code and state"]
  Back --> Code
  Code -- "yes" --> State{"state matches<br/>this tab's sign-in?"}
  State -- "no" --> Reject["Rejected: no token request<br/>(stays signed out)"]
  State -- "yes" --> Exchange["POST /token with the code<br/>and PKCE verifier"]
  Exchange --> Load

  Load["Load the session once, in parallel:<br/>GET /health, /health/ready,<br/>/api/me, /api/departments"] --> Workspace["The page from returnUrl,<br/>or /dashboard (see section 2)"]
  Workspace -- "API answers 401<br/>(token expired)" --> SignIn
  Workspace -- "Sign out" --> Logout["Clear this app's session keys,<br/>Entra logout (mock: /signed-out)"]
  Logout --> SignIn
```

The Entra redirect still returns to the site root (`/`), so the app registration doesn't change. An app initializer exchanges the code before the router starts, then replaces the address with the saved `returnUrl`.

What the caller sees before and after access is known:

| Situation | What the user sees |
| --- | --- |
| API or SQL Server down | Dashboard panels say they couldn't load; the status line shows *API unavailable* or *Database not ready* |
| Signed in, but no application profile, or the profile is inactive (`/api/me` is 403) | `/access-denied`, with the account's Object ID to provision |
| Signed in and provisioned | The dashboard tiles and panels their permissions allow (section 2); each panel loads separately, so one failure doesn't blank the page |

## 2. Workspace pages and what controls each section

```mermaid
flowchart TD
  Page["Shell layout<br/>(sidebar, top bar, user menu)"] --> Overview["/dashboard<br/>my expenses, approvals waiting,<br/>budget this month, unread updates"]
  Page --> Activity["/notifications<br/>any provisioned user"]
  Page --> Expenses["/expenses<br/>any provisioned user"]
  Page --> Budgets["/budgets<br/>Budget vs. actual"]
  Page --> Reports["/reports"]

  Page --> Outbox["/admin/outbox<br/>Outbox status, dead letters, Replay<br/>needs messaging.manage"]

  Expenses --> Mine["Tab: My expenses (always)"]
  Expenses --> Queue["Tab: Approval queue<br/>needs expenses.approve"]
  Expenses --> ToPost["Tab: Ready to post<br/>needs expenses.post"]
  Expenses --> All["Tab: All visible<br/>needs expenses.read.department or .all"]
  Expenses --> New["New expense button<br/>needs expenses.create and an active membership"]
  Expenses --> Detail["Expense detail panel<br/>buttons = allowedActions from the API"]

  Budgets -.->|"hidden without"| BRead["budgets.read.department or .all"]
  Budgets --> SetBudget["Set budget / Edit<br/>needs budgets.manage"]

  Reports -.->|"hidden when the catalog is empty"| RCat["catalog from GET /api/reports"]
  Reports --> Preview["Preview table"]
  Reports --> Export["PDF and Excel download (audited)"]
```

### Who sees what (demo personas)

From the seeded roles and memberships (`seed-demo-data`):

| Persona | Role | Departments | Expense tabs | New expense | Budgets | Reports | Outbox |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Demo Employee | Employee | Engineering | My expenses | Yes | Hidden | Hidden | Hidden |
| Demo Engineering Manager | Manager | Engineering | My expenses, Approval queue, All visible | Yes | Engineering, read only | 4, Engineering only | Hidden |
| Demo Manager | Manager | Finance (Operations expired) | My expenses, Approval queue, All visible | Yes | Finance, read only | 4, Finance only | Hidden |
| Demo Finance Officer | FinanceOfficer | Finance | My expenses, Ready to post, All visible | Yes | All, can set budgets | 4, all departments | Hidden |
| Demo Administrator | Administrator | None (global read) | My expenses, All visible | No | All, read only | All 5, including *Audit activity* | Yes, with Replay |
| Demo Former Employee | Employee (inactive) | Engineering | None: `/api/me` is 403 | No | Hidden | Hidden | Hidden |

The four reports open to managers and finance are *Department expense summary*, *Budget vs. actual*, *Pending approval aging* and *Transaction ledger*. *Audit activity* needs `audit.read`.

## 3. An expense from draft to reimbursement

The buttons in the detail panel come from the API's `allowedActions` for that caller and that expense. The domain enforces the same rules, including separation of duties: nobody approves or posts their own expense.

```mermaid
stateDiagram-v2
  [*] --> Draft: New expense → Create draft
  Draft --> Draft: Edit (owner)
  Draft --> Submitted: Submit for approval (owner)
  Draft --> Cancelled: Cancel (owner)
  Submitted --> Draft: Withdraw (owner)
  Submitted --> Approved: Approve (manager of the department, not the owner)
  Submitted --> Rejected: Reject with a comment (manager, not the owner)
  Rejected --> Draft: Revise (owner)
  Approved --> Posted: Post to ledger (finance officer, not the owner)
  Posted --> [*]
  Cancelled --> [*]
```

Every change sends `If-Match` with the expense's ETag. If someone else changed it first, the UI shows *Modified by someone else* (412), and the user reloads and decides again.

After a decision or a posting, the background work shows up later in the page:

```mermaid
sequenceDiagram
  actor Manager
  actor Finance as Finance officer
  actor Owner as Expense owner
  participant UI as Angular page
  participant API
  participant SQL as SQL Server (with outbox)
  participant Worker as Outbox worker

  Manager->>UI: Approve
  UI->>API: POST /api/expenses/{id}/approve (If-Match)
  API->>SQL: status + audit row + outbox message, one transaction
  Finance->>UI: Post to ledger
  UI->>API: POST /api/expenses/{id}/post (If-Match)
  API->>SQL: status + audit row + outbox message, one transaction
  Worker->>SQL: claim messages (peek-lock)
  Worker->>SQL: schedule reimbursement, then write notifications
  Owner->>UI: Notifications → Refresh
  UI->>API: GET /api/notifications
  API-->>UI: "approved" and "posted" notifications
  Note over UI: The expense detail now shows Reimbursement: Scheduled
```

## Related

- [Architecture overview](overview.md)
- [Identity and authorization](../security/identity-authorization.md)
- [Demo walkthrough](../demo-walkthrough.md), a scripted tour of these screens
- API reference: Swagger UI at `https://localhost:7143/swagger` when the API runs in Development, or [docs/api/openapi.json](../api/openapi.json)
