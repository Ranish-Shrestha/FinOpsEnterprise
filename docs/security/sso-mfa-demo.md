# Free SSO and MFA demo (Microsoft and Google)

How to show real single sign-on and multi-factor authentication from the local UI, at no cost. Everything runs on your machine except the identity provider: a free Microsoft Entra tenant, plus optionally a free Google Cloud OAuth client.

| Piece | Cost |
| --- | --- |
| Entra tenant, app registrations, sign-in | Free (Entra ID Free) |
| MFA through security defaults or per-user MFA, with the Microsoft Authenticator app | Free |
| Google federation and Gmail guest users | Free (guests are billed by monthly active users, with a large free allowance) |
| Google Cloud OAuth client | Free |
| Conditional Access | **Paid (Entra ID P1). Not used here** |

Mock mode can't show any of this: it skips the identity provider. This guide switches the local app to **Entra mode**. To switch back afterwards, see the [last section](#back-to-mock-mode).

What you'll demonstrate:

1. **Microsoft SSO + MFA.** A member user signs in with Microsoft and approves an Authenticator prompt. The dashboard *Sign-in* card says **MFA verified**.
2. **Google SSO.** A Gmail user, invited as a guest, signs in with Google. The card names **Google** as the identity provider.
3. **Authorization still comes from the app.** A new account lands on `/access-denied`, which shows its object ID. Once you provision it, it sees exactly what its role allows.

## 1. Tenant and app registrations (once)

1. **Create a free tenant.** Sign in at https://entra.microsoft.com with any Microsoft account. Go to *Identity* → *Overview* → *Manage tenants* → *Create* → **Microsoft Entra ID**.
   - A dedicated demo tenant keeps test users away from anything real.
   - The Azure free account sign-up may ask for a card to verify your identity. Nothing in this guide charges it.
2. **Register the API and the SPA** as in [Entra registration](identity-authorization.md#entra-registration-real-sso), steps 1–4:
   - API: expose the `FinOps.Access` scope, with `requestedAccessTokenVersion: 2`.
   - SPA: redirect URI `http://127.0.0.1:4200/`, no secret, and the `FinOps.Access` delegated permission.
   - API user secrets: `Security:TenantId`, `Security:Audience`, `Security:UseDevelopmentMockAuthentication=false`.
   - `src/finops-web/public/config.json`: `mockMode: false`, plus the tenant ID, SPA client ID and API scope. Don't commit it.
3. **Start SQL Server, the API, the worker and the UI** as in the README. Open http://127.0.0.1:4200. `/login` now shows **Sign in with Microsoft**.

## 2. Microsoft SSO with MFA

1. **Create a member user:** *Users* → *All users* → *New user* → *Create new user*. For example `demo.manager@<your-tenant>.onmicrosoft.com`, with an auto-generated password. Copy the password.
2. **Require MFA for that user.** Pick one of these free options:
   - **Per-user MFA** (best for a demo, because it asks on every sign-in):
     - Go to *Users* → *All users* → **Per-user MFA** (in the toolbar).
     - Select the user and **Enable** them.
     - The portal won't let you use this while security defaults are on. If so, first go to *Identity* → *Overview* → *Properties* → *Manage security defaults* → **Disabled**.
   - **Security defaults** (on by default in new tenants): every user must register Authenticator, but Entra decides when to prompt, so a given sign-in may not ask.
3. **Sign in.** Open a **private window** at http://127.0.0.1:4200 and click *Sign in with Microsoft*.
   - Sign in as the demo user and change the temporary password.
   - Register **Microsoft Authenticator** when asked. It's free on iOS and Android.
   - Approve the prompt. You're sent back to the app.
4. **Provision the user.** The app shows **You don't have access**, with the account's **Object ID**. Copy it and run:

   ```powershell
   dotnet run --project src/FinOps.Api -- provision-user --object-id <object-id> `
     --name "Demo Manager" --email demo.manager@<your-tenant>.onmicrosoft.com `
     --role Manager --department "Demo Engineering"
   ```

   - Roles: `Employee`, `Manager`, `FinanceOfficer`, `Administrator`.
   - Departments: `Demo Engineering`, `Demo Finance`, `Demo Operations`. Leave out `--department` for `FinanceOfficer` and `Administrator`.
   - The command is idempotent, and uses the same rules as [infra/sql/provision-user.sql](../../infra/sql/provision-user.sql).
5. **Click *Try again*.** The dashboard's **Sign-in** card shows:
   - **MFA verified**;
   - *Microsoft Entra ID · pwd, mfa*, read by the API from the validated token's `amr` claim (`GET /api/me/sign-in`).

## 3. Google SSO (Gmail guest)

Entra federates with Google for **Gmail** guests. Google redirects to Microsoft, never to localhost, so nothing changes for the local app.

1. **Google Cloud Console** (https://console.cloud.google.com), with a new project:
   - **OAuth consent screen:**
     - Choose *External*, and add `microsoftonline.com` under *Authorized domains*.
     - Leave it in **Testing** and add your Gmail address as a **test user**. Testing mode is free and needs no Google verification.
   - **Credentials** → *Create credentials* → *OAuth client ID* → *Web application*. Under **Authorized redirect URIs** add:

     ```
     https://login.microsoftonline.com
     https://login.microsoftonline.com/te/<tenant-id>/oauth2/authresp
     https://login.microsoftonline.com/te/<tenant-name>.onmicrosoft.com/oauth2/authresp
     ```

   - Copy the **client ID** and **client secret**. The secret goes into Entra only, never into this repository.
2. **Entra admin center:** *External Identities* → *All identity providers* → **Google**, then paste the client ID and secret.
3. **Invite the Gmail address:** *Users* → *All users* → *New user* → **Invite external user**. Open the invitation email in that Gmail account and accept it with Google.
4. **Sign in.** In a private window, open http://127.0.0.1:4200 → *Sign in with Microsoft* → type the **Gmail address**. Entra sends you to Google, then back to the app.
5. **Provision the guest** with `provision-user`, using the Object ID from `/access-denied` (the guest's ID in your tenant), then click *Try again*.
   - The Sign-in card shows **Google**, and usually *No MFA in token*.
   - Google's own 2-step verification doesn't appear in the Entra token.
   - To demonstrate MFA for this user too, enable per-user MFA for the guest account, as in section 2. Entra then asks for Authenticator after the Google step.

Google blocks sign-in inside embedded web views. This app uses the normal browser, so that's not an issue here.

## 4. Proof for the audience

- **Dashboard → Sign-in card:** the identity provider and `amr` methods from the token the API just validated.
- **Entra admin center → *Monitoring* → *Sign-in logs*:** open the sign-in.
  - *Authentication Details* lists the methods: password, Authenticator, or the federated Google step.
  - *Authentication requirement* says **Multifactor authentication**.
  - The Free tier keeps these logs for about 7 days.
- **Authorization is separate:** sign in as a user with a different role. Pages they may not use are missing from the sidebar, and the API answers 403 even if you call it directly.

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| `AADSTS50011` redirect URI mismatch | The SPA registration needs exactly `http://127.0.0.1:4200/`, including the trailing slash, under the *Single-page application* platform |
| Every API call is 401 | Check `Security:Audience`. With v2 tokens, `aud` is the API's client ID GUID, so try that instead of `api://…`. Also confirm `requestedAccessTokenVersion: 2` |
| Signed in, but always `/access-denied` | Expected until `provision-user` runs. Use the Object ID shown on the page, not the email address |
| Google `redirect_uri_mismatch` | Add all three Microsoft redirect URIs in the Google OAuth client |
| Google "Access blocked" or "app not verified" | Add the Gmail address as a test user on the OAuth consent screen (Testing mode) |
| No MFA prompt | Security defaults prompt only when Entra decides to. Use per-user MFA for a predictable demo, and sign in from a private window |
| Card says *Not reported* | The token had no `amr` claim. Check the sign-in logs for the methods actually used |

## Back to mock mode

- `config.json`: set `"mockMode": true`, or revert the file with `git checkout src/finops-web/public/config.json`.
- `dotnet user-secrets set "Security:UseDevelopmentMockAuthentication" "true" --project src/FinOps.Api`

Provisioned real users stay in the database. They never affect the synthetic personas.
