# Deploying to Azure

One-time setup, then every push to `main` that passes CI deploys automatically. The design and its costs are in [ADR 0007](../decisions/0007-azure-deployment.md).

**Before you start:**

- **It costs money.** Expect close to zero when idle, but check the [Azure pricing calculator](https://azure.microsoft.com/pricing/calculator/) for your region first. Set a budget alert on the resource group.
- **Synthetic data only.** The deployed app must hold no real financial or personal data. The only real identities are the Entra users you provision.
- **You need:**
  - an Azure subscription where you can create resource groups and app registrations;
  - a Microsoft Entra tenant, which can be the subscription's;
  - the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli).

```text
GitHub Actions (OIDC) ──► Azure Resource Group
                           ├─ Static Web App (Free) ......... Angular SPA + config.json
                           ├─ Container Apps environment
                           │    └─ finops-api (0–2 replicas) . API + in-process outbox processing
                           ├─ Azure SQL serverless (free offer, Entra-only auth)
                           ├─ Managed identity .............. the API's identity for SQL
                           └─ Log Analytics + App Insights .. logs, traces (OpenTelemetry agent)
Images: ghcr.io/<owner>/finops-api, finops-worker (public)
```

## 1. Sign in and create the resource group

```powershell
az login
az account set --subscription "<subscription id>"
az group create --name rg-finops-demo --location eastus2
```

## 2. Create the deployment identity (OIDC, no secret)

This app registration is what GitHub Actions signs in as. It also becomes the SQL server's Entra administrator, so it can run migrations.

```powershell
$app = az ad app create --display-name "finops-github-deploy" | ConvertFrom-Json
$sp  = az ad sp create --id $app.appId | ConvertFrom-Json
az role assignment create --assignee-object-id $sp.id --assignee-principal-type ServicePrincipal `
  --role Contributor --scope (az group show --name rg-finops-demo --query id --output tsv)

# Trust GitHub's OIDC tokens from the "production" environment of your repository only.
@'
{ "name": "github-production", "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:<owner>/<repo>:environment:production", "audiences": ["api://AzureADTokenExchange"] }
'@ | Set-Content federated.json
az ad app federated-credential create --id $app.appId --parameters federated.json
Remove-Item federated.json
"AZURE_CLIENT_ID=$($app.appId)  SQL_ADMIN_OBJECT_ID=$($sp.id)  SQL_ADMIN_NAME=finops-github-deploy"
```

*Contributor* on the one resource group is enough. The templates create no role assignments.

## 3. Register the API and the SPA in Entra

Follow [Entra registration](../security/identity-authorization.md#entra-registration-real-sso), with these differences:

- **SPA redirect URIs:** keep `http://127.0.0.1:4200/` for local use. Add the Static Web App URL after the first deployment (step 6), because Azure generates that host name.
- **The two values you need:**
  - the API's **audience**: its client ID with v2 tokens, or its `api://…` URI;
  - its **scope**: `api://<api client id>/FinOps.Access`.

## 4. Configure the GitHub repository

1. Create an environment named **`production`** (*Settings → Environments*). Optionally require a reviewer before each deployment.
2. Add these **variables** (*Settings → Secrets and variables → Actions → Variables*). They are identifiers, not secrets:

   | Variable | Value |
   | --- | --- |
   | `AZURE_CLIENT_ID` | Deployment app's client ID (step 2) |
   | `AZURE_TENANT_ID` | Tenant of the subscription |
   | `AZURE_SUBSCRIPTION_ID` | Subscription ID |
   | `AZURE_RESOURCE_GROUP` | `rg-finops-demo` |
   | `SQL_ADMIN_NAME` | `finops-github-deploy` |
   | `SQL_ADMIN_OBJECT_ID` | Deployment service principal's object ID (step 2) |
   | `ENTRA_TENANT_ID` | Tenant that issues the app's user tokens (often the same) |
   | `ENTRA_API_AUDIENCE` | API audience (step 3) |
   | `ENTRA_API_SCOPE` | `api://<api client id>/FinOps.Access` |
   | `ENTRA_SPA_CLIENT_ID` | SPA app registration's client ID |
   | `DEPLOY_WORKER` | Optional. `true` runs a separate, always-on worker app (more cost; see ADR 0007) |
   | `AZURE_DEPLOY_ENABLED` | `true`, last, once everything above is set |

**Secrets:** none. GitHub signs in to Azure with OIDC. The Static Web Apps token is fetched during each deployment and masked.

## 5. Deploy

Run the **Deploy** workflow (*Actions → Deploy → Run workflow* on `main`), or push to `main`. It runs these jobs:

1. **Images:** builds `finops-api` and `finops-worker`, tags them with the commit SHA, and pushes them to GHCR with SBOM and provenance.
2. **Infrastructure:** deploys `infra/main.bicep` and starts the API revision.
3. **Database:**
   - `migrate` applies the EF migrations.
   - `grant-app-access <identity> <client id>` creates the app's database user with least privilege.
4. **Web:** builds the SPA once, writes `config.json` (Entra mode, API URL) and the CSP's API origin, then uploads to the Static Web App.
5. **Smoke test:**
   - `/health` and `/health/ready` return 200;
   - an anonymous `/api/me` returns 401;
   - the SPA serves an Entra-mode `config.json` and a CSP that names the API.

**First run only:** GHCR packages start private, and Container Apps pulls without credentials. After the *Images* job has pushed once:

1. Make both packages public (*your GitHub profile → Packages → finops-api/finops-worker → Package settings → Change visibility*).
2. Re-run the failed jobs.

## 6. After the first deployment

1. **SPA redirect URI.** Add the deployed SPA URL (the *Infrastructure* job's `webUrl`, for example `https://<name>.azurestaticapps.net/`) to the SPA app registration.
2. **Provision users.**
   - Users need an application profile before they can sign in.
   - Run [infra/sql/provision-user.sql](../../infra/sql/provision-user.sql) as the SQL Entra admin: for example, the Azure portal's *Query editor* signed in as a member of the admin group, or temporarily make yourself the admin.
   - Fill in the user's Entra **object ID**, name, email, role and department.
3. **Sign in** at the SPA URL. Expect the first request after idle time to take tens of seconds (cold start and database resume).

## Operating it

| Task | How |
| --- | --- |
| Logs | Log Analytics: `ContainerAppConsoleLogs_CL`, or Application Insights → *Logs* |
| Traces | Application Insights → *Transaction search*. Requests, SQL and outbox processing share one operation ID |
| Dead letters | Sign in as an administrator → *Notifications → Outbox* → *Replay* |
| Roll back | Re-run **Deploy** for an earlier commit, or `az containerapp revision list` and activate the previous revision |
| Pause spending | `az containerapp update -n finops-api -g rg-finops-demo --max-replicas 0` (SQL pauses on its own) |
| Delete everything | `az group delete --name rg-finops-demo`, then delete the deployment app registration and the GHCR packages |

## Local checks of the deployable pieces

```powershell
docker build --target api -t finops-api .
docker build --target worker -t finops-worker .
docker run --rm finops-api seed-demo-data   # refused: the image runs in Production
```

CI builds both images and compiles and lints the Bicep on every push (`deployables` job). `DeploymentTests` run the whole business flow with only the runtime database grants.
