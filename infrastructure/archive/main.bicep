// ARC-009 — first cloud release: empty archive shell on scale-to-zero
// Container Apps. Resource-group scope, deployed to RG-Liedertafel-Archive.
//
// Scope notes:
// - This template owns topology only (environment, app revisions config,
//   vault, workspace, identities). It never invents the running image: the
//   release workflow supplies containerImage by immutable digest, and the
//   infra workflow passes the currently deployed image through so an
//   infrastructure re-run cannot revert a release.
// - No storage account, queues, Neon or email resources here. Live Blob
//   integration is ARC-049, hosted sign-in ARC-011, real email ARC-010.
// - No database connection is configured: the shell boots dependency-free
//   (DbContext resolves lazily; /alive, /api/build and static assets never
//   touch it). DB-backed endpoints return a German 500 Problem until ARC-011
//   wires Neon.
targetScope = 'resourceGroup'

@description('Azure region for all archive resources. Austria East preferred; West Europe is the fallback.')
param location string = 'austriaeast'

@description('Container Apps environment name.')
param environmentName string = 'cae-liedertafel-archive'

@description('Container app name. Single origin serves the frontend and /api/*.')
param appName string = 'ca-liedertafel-archive'

@description('Runtime user-assigned managed identity name.')
param runtimeIdentityName string = 'id-archive-app'

@description('Key Vault name (RBAC model). Holds the GHCR pull credential lifecycle; runtime secrets arrive in ARC-011.')
param vaultName string = 'kv-liedertafel-archive'

@description('Log Analytics workspace name.')
param workspaceName string = 'log-liedertafel-archive'

@description('Full image reference including immutable digest (ghcr.io/<owner>/liedertafel-archive@sha256:...). No default on purpose.')
param containerImage string

@description('GHCR username for private image pulls (lowercase owner).')
param ghcrUsername string = 'jakobferdinand'

@secure()
@description('GHCR pull credential: fine-grained PAT with packages:read. Supplied at deploy time, never logged or baked into the image.')
param ghcrPassword string

@description('Public archive hostname.')
param customDomain string = 'archiv.liedertafel-mining.at'

@description('Bind the custom domain with a managed certificate. Requires the CNAME and asuid TXT records first; keep false until DNS is in place.')
param bindCustomDomain bool = false

@description('Log Analytics daily ingestion cap in GB. ARC-009 decision: 1 GB/day; the shell needs far less and PAYG retention stays at 31 days.')
param logAnalyticsDailyCapGb int = 1

@description('Deploy the RBAC role assignments. PR what-if uses a Contributor-only preview identity without Microsoft.Authorization/roleAssignments/write, so it previews with false; real deploys keep true.')
param deployRoleAssignments bool = true

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: workspaceName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 31
    workspaceCapping: {
      dailyQuotaGb: logAnalyticsDailyCapGb
    }
  }
}

resource runtimeIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: runtimeIdentityName
  location: location
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: vaultName
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enabledForDeployment: false
    enabledForDiskEncryption: false
    enabledForTemplateDeployment: false
  }
}

// ARC-004 contract: runtime identity may read vault secrets (GHCR pull
// lifecycle moves here in ARC-011) and publish monitoring metrics.
// Skipped in PR what-if: the Contributor-only preview identity lacks
// Microsoft.Authorization/roleAssignments/write by design.
// Role diffs are reviewed via code and covered by the privileged deploy.
resource vaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployRoleAssignments) {
  name: guid(vault.id, runtimeIdentity.id, 'secrets-user')
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '4633458b-17de-408a-b874-0445c86b69e6'
    )
    principalId: runtimeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource metricsPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployRoleAssignments) {
  name: guid(resourceGroup().id, runtimeIdentity.id, 'metrics-publisher')
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '3913510d-42f4-4e42-8a64-420c390055eb'
    )
    principalId: runtimeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: workspace.properties.customerId
        sharedKey: workspace.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    zoneRedundant: false
  }
}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${runtimeIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      maxInactiveRevisions: 2
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
        customDomains: bindCustomDomain
          ? [
              {
                name: customDomain
                bindingType: 'SniEnabled'
                certificateId: certificate.id
              }
            ]
          : []
      }
      registries: [
        {
          server: 'ghcr.io'
          username: ghcrUsername
          passwordSecretRef: 'ghcr-password'
        }
      ]
      secrets: [
        {
          name: 'ghcr-password'
          value: ghcrPassword
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'archive'
          image: containerImage
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          probes: [
            {
              type: 'Startup'
              httpGet: {
                path: '/alive'
                port: 8080
              }
              initialDelaySeconds: 2
              periodSeconds: 3
              failureThreshold: 20
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/alive'
                port: 8080
              }
              initialDelaySeconds: 5
              periodSeconds: 10
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/alive'
                port: 8080
              }
              initialDelaySeconds: 5
              periodSeconds: 5
              failureThreshold: 3
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 2
        rules: [
          {
            name: 'http'
            http: {
              metadata: {
                concurrentRequests: '10'
              }
            }
          }
        ]
      }
    }
  }
}

// Managed certificate with CNAME domain control validation. Created only
// after the maintainer adds the CNAME plus asuid TXT records at World4You
// (see infrastructure/archive/README.md); otherwise issuance fails.
resource certificate 'Microsoft.App/managedEnvironments/managedCertificates@2024-03-01' = if (bindCustomDomain) {
  parent: environment
  name: 'archiv-cert'
  location: location
  properties: {
    subjectName: customDomain
    domainControlValidation: 'CNAME'
  }
}

output environmentDefaultDomain string = environment.properties.defaultDomain
output appFqdn string = app.properties.configuration.ingress.fqdn
output vaultUri string = vault.properties.vaultUri
output runtimeIdentityPrincipalId string = runtimeIdentity.properties.principalId
output customDomainBound bool = bindCustomDomain
