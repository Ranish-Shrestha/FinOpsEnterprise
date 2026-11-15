// FinOps Enterprise: low-cost Azure deployment (ADR 0007).
//
//   Static Web Apps (Free)          Angular SPA, served with config.json written by the deploy workflow
//   Container Apps (Consumption)    API (scale to zero; drains the outbox while running) [+ optional worker]
//   Azure SQL Database serverless   free offer, Microsoft Entra-only authentication
//   User-assigned managed identity  the apps' identity for SQL: no passwords anywhere
//   Log Analytics + App Insights    container logs; traces and logs via the environment's OpenTelemetry agent
//
// No secrets are created or passed: SQL uses managed identity, images come from a public registry, and the Entra
// values are public identifiers. Deploy with .github/workflows/deploy.yml (see docs/operations/deployment.md).

targetScope = 'resourceGroup'

@description('Region for everything except the Static Web App.')
param location string = resourceGroup().location

@description('Static Web Apps is offered in a few regions only.')
@allowed(['westus2', 'centralus', 'eastus2', 'westeurope', 'eastasia'])
param staticWebAppLocation string = 'eastus2'

@description('Short prefix for resource names.')
@minLength(3)
@maxLength(12)
param namePrefix string = 'finops'

@description('API container image, for example ghcr.io/owner/finops-api:<git sha>.')
param apiImage string

@description('Worker container image. Used only when deployWorker is true.')
param workerImage string = ''

@description('Run FinOps.Worker as its own always-on container app. False: the API drains the outbox while it runs, so nothing polls SQL when idle and the serverless database can pause (ADR 0007).')
param deployWorker bool = false

@description('Microsoft Entra tenant that issues tokens for the API.')
param entraTenantId string

@description('Expected token audience: the API app registration\'s client ID (v2 tokens) or its api:// URI.')
param apiAudience string

@description('Display name of the SQL Server Microsoft Entra administrator (the deploying service principal, or a group containing it).')
param sqlAdminLogin string

@description('Object ID of that administrator.')
param sqlAdminObjectId string

@allowed(['Application', 'Group', 'User'])
param sqlAdminPrincipalType string = 'Application'

@description('Daily Log Analytics ingestion cap in GB; protects the budget from a log storm.')
param logDailyCapGb string = '0.5'

param tags object = {
  application: 'finops-enterprise'
  environment: 'demo'
  data: 'synthetic-only'
}

var suffix = uniqueString(resourceGroup().id)
var apiAppName = '${namePrefix}-api'
var databaseName = 'finops'

// ---------- Observability ----------

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-logs-${suffix}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: json(logDailyCapGb) }
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${namePrefix}-insights-${suffix}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
    DisableLocalAuth: false
  }
}

// ---------- Identity ----------

resource appIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-app-id'
  location: location
  tags: tags
}

// ---------- SQL ----------

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: '${namePrefix}-sql-${suffix}'
  location: location
  tags: tags
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    // Entra-only: there is no SQL login or password to leak.
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      principalType: sqlAdminPrincipalType
      tenantId: tenant().tenantId
    }
  }
}

// Container Apps (Consumption) has no fixed outbound IPs, so Azure services are allowed through the firewall.
// Entra-only authentication is what protects the database; a VNet with a private endpoint is the next step (ADR 0007).
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: databaseName
  location: location
  tags: tags
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 1
  }
  properties: {
    // Azure SQL Database free offer: 100,000 vCore-seconds and 32 GB per month. When the monthly allowance is spent
    // the database pauses until next month instead of billing.
    useFreeLimit: true
    freeLimitExhaustionBehavior: 'AutoPause'
    autoPauseDelay: 60
    minCapacity: json('0.5')
    maxSizeBytes: 34359738368
    zoneRedundant: false
    requestedBackupStorageRedundancy: 'Local'
  }
}

// ---------- SPA ----------

resource web 'Microsoft.Web/staticSites@2023-12-01' = {
  name: '${namePrefix}-web-${suffix}'
  location: staticWebAppLocation
  tags: tags
  sku: { name: 'Free', tier: 'Free' }
  properties: {
    allowConfigFileUpdates: true
    stagingEnvironmentPolicy: 'Disabled'
  }
}

// ---------- Container Apps ----------

resource environment 'Microsoft.App/managedEnvironments@2024-10-02-preview' = {
  name: '${namePrefix}-env-${suffix}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
    // The managed OpenTelemetry agent sets OTEL_EXPORTER_OTLP_ENDPOINT in every app, which switches on the apps'
    // own telemetry export (ADR 0006) and forwards traces and logs to Application Insights.
    appInsightsConfiguration: {
      connectionString: insights.properties.ConnectionString
    }
    openTelemetryConfiguration: {
      tracesConfiguration: { destinations: ['appInsights'] }
      logsConfiguration: { destinations: ['appInsights'] }
    }
    zoneRedundant: false
  }
}

var sqlConnectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${databaseName};Authentication=Active Directory Managed Identity;User Id=${appIdentity.properties.clientId};Encrypt=True;TrustServerCertificate=False;Connect Timeout=60'

var sharedEnv = [
  { name: 'ConnectionStrings__FinOps', value: sqlConnectionString }
  { name: 'AZURE_CLIENT_ID', value: appIdentity.properties.clientId }
]

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: apiAppName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${appIdentity.id}': {} }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: 'api'
          image: apiImage
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: concat(sharedEnv, [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
            // Ingress routes only this app's host name to the container; the platform's probes call the container
            // directly by IP, so a host allow-list here would fail every probe. Host filtering is done by ingress.
            { name: 'AllowedHosts', value: '*' }
            { name: 'Security__TenantId', value: entraTenantId }
            { name: 'Security__Audience', value: apiAudience }
            { name: 'Cors__AllowedOrigins__0', value: 'https://${web.properties.defaultHostname}' }
            { name: 'Messaging__RunInApi', value: string(!deployWorker) }
          ])
          // Probes use the process-only /health: readiness on /health/ready would hold every cold start until the
          // serverless database resumes. /health/ready stays available for availability monitoring.
          probes: [
            { type: 'Startup', httpGet: { path: '/health', port: 8080 }, periodSeconds: 3, failureThreshold: 20 }
            { type: 'Liveness', httpGet: { path: '/health', port: 8080 }, periodSeconds: 30 }
            { type: 'Readiness', httpGet: { path: '/health', port: 8080 }, periodSeconds: 10 }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 2
        rules: [
          { name: 'http', http: { metadata: { concurrentRequests: '50' } } }
        ]
      }
    }
  }
}

resource worker 'Microsoft.App/containerApps@2024-03-01' = if (deployWorker) {
  name: '${namePrefix}-worker'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${appIdentity.id}': {} }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: { activeRevisionsMode: 'Single' }
    template: {
      containers: [
        {
          name: 'worker'
          image: workerImage
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: concat(sharedEnv, [
            { name: 'DOTNET_ENVIRONMENT', value: 'Production' }
          ])
        }
      ]
      // A polling worker cannot scale to zero; one replica is enough (claims are safe with more).
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

// ---------- Outputs (no secrets) ----------

output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
output webUrl string = 'https://${web.properties.defaultHostname}'
output staticWebAppName string = web.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output databaseName string = databaseName
output appIdentityName string = appIdentity.name
