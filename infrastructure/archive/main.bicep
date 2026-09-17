// ARC-009 — first cloud release: empty archive shell on scale-to-zero
// Container Apps. Resource-group scope, deployed to RG-Liedertafel-Archive.
// ARC-011 — hosted persistent sign-in: private Blob key ring protected by a
// Key Vault wrapping key, Neon runtime connection plus operator token via
// Key Vault references, and keyless Azure mail through the runtime identity.
//
// Scope notes:
// - This template owns topology only (environment, app revisions config,
//   vault, workspace, identities). It never invents the running image: the
//   release workflow supplies containerImage by immutable digest, and the
//   infra workflow passes the currently deployed image through so an
//   infrastructure re-run cannot revert a release.
// - No queues, Neon or email resources here. The key-ring storage account
//   below is the only Blob integration in this template; member-file Blob
//   integration stays ARC-049. Hosted sign-in is ARC-011, real email ARC-010.
// - No database connection is baked in: the Neon runtime connection arrives
//   as a Key Vault reference (secret `archive-db-connection`, placed by the
//   maintainer per infrastructure/archive/README.md). The shell still boots
//   dependency-free (/alive, /api/build and static assets never touch the
//   database); DB-backed endpoints return a German 500 Problem until the
//   ARC-011 secret is placed.
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

@description('Attach the custom domain as a hostname on the app ingress (HTTP until the certificate stage binds TLS). Requires the CNAME record first; keep false until DNS is in place.')
param bindCustomDomain bool = false

@description('Issue the managed CNAME-validated certificate and bind it to the custom hostname. Second stage: run once with only bindCustomDomain after the hostname is attached, then enable this. A fresh environment always needs the hostname-first run because certificate issuance requires the attached hostname.')
param bindManagedCertificate bool = false

@description('Log Analytics daily ingestion cap in GB. ARC-009 decision: 1 GB/day; the shell needs far less and PAYG retention stays at 31 days.')
param logAnalyticsDailyCapGb int = 1

@description('Deploy the RBAC role assignments. PR what-if uses a Contributor-only preview identity without Microsoft.Authorization/roleAssignments/write, so it previews with false; real deploys keep true.')
param deployRoleAssignments bool = true

@description('Archive storage account name for the private Data Protection key ring (ARC-011). Lowercase alphanumeric, 3-24 chars, globally unique.')
param storageAccountName string = 'stliedertafelarchive'

@description('Private blob container holding the Data Protection key ring.')
param keysContainerName string = 'dataprotection'

@description('Blob object name for the Data Protection key ring inside the container.')
param keysBlobName string = 'keys.xml'

@description('Key Vault RSA wrapping-key name protecting the Data Protection key ring (ARC-011).')
param keysKeyName string = 'dataprotection-wrap'

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

// ARC-011 — private key-ring storage. No anonymous blob access; Entra-only
// (shared-key access off, OAuth default). Consumption has no VNet injection,
// so the public endpoint stays enabled but every blob call authenticates via
// the runtime identity (Storage Blob Data Contributor below).
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'
    accessTier: 'Hot'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource keysContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: keysContainerName
  properties: {
    publicAccess: 'None'
  }
}

// ARC-011 — RSA wrapping key for the Data Protection ring. Standard vault is
// enough; no HSM/Premium needed. The backend references the versionless key
// URI so Key Vault rotation needs no app change; old key versions stay
// enabled so still-valid cookies keep decrypting.
resource keysKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: vault
  name: keysKeyName
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: [
      'wrapKey'
      'unwrapKey'
    ]
    attributes: {
      enabled: true
    }
  }
}

// ARC-011: runtime reads/writes the key-ring blob (rotation must write) and
// wraps/unwraps keys. Skipped in PR what-if like the other RBAC assignments.
resource blobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployRoleAssignments) {
  name: guid(storage.id, runtimeIdentity.id, 'blob-contributor')
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
    )
    principalId: runtimeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource cryptoUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployRoleAssignments) {
  name: guid(vault.id, runtimeIdentity.id, 'crypto-user')
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '12338af0-0e69-4776-bea7-57ae8d297424'
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
              union(
                {
                  name: customDomain
                  bindingType: bindManagedCertificate ? 'SniEnabled' : 'Disabled'
                },
                // Only referenced once the certificate stage is enabled; the
                // hostname-first run deploys without it.
                bindManagedCertificate ? { certificateId: certificate.id } : {}
              )
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
        // ARC-011: versionless Key Vault URLs rotate without a Bicep change.
        // The maintainer places both secrets out of band (see README); the
        // Neon value is the archive_runtime connection only, never the
        // migrator/admin credential or a Neon API key.
        {
          name: 'archive-db-connection'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/archive-db-connection'
          identity: runtimeIdentity.id
        }
        {
          name: 'archive-operator-token'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/archive-operator-token'
          identity: runtimeIdentity.id
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
          // ARC-010: Production refuses to boot on local SMTP capture, so the
          // hosted shell selects the Azure provider with the verified
          // choir-domain endpoint. Sends stay keyless via the runtime managed
          // identity; the transport builds lazily, so /alive, /api/build and
          // static assets stay dependency-free until the first send.
          // ARC-011: the Neon runtime connection and operator token arrive as
          // Key Vault references (the API never sees migrator/admin
          // credentials or Neon API keys). AZURE_CLIENT_ID pins
          // DefaultAzureCredential to the user-assigned identity for mail,
          // Blob keys and Key Vault unwrap. The key-ring URIs carry no
          // secrets; rotation (Blob ring append, versionless KV key) needs no
          // Bicep change.
          env: [
            {
              name: 'Mail__Provider'
              value: 'Azure'
            }
            {
              name: 'Mail__AzureEndpoint'
              value: 'https://acs-liedertafel-archive.europe.communication.azure.com'
            }
            {
              name: 'ConnectionStrings__archive-db'
              secretRef: 'archive-db-connection'
            }
            {
              name: 'Archive__OperatorToken'
              secretRef: 'archive-operator-token'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: runtimeIdentity.properties.clientId
            }
            {
              name: 'Authentication__KeysBlobUri'
              value: 'https://${storage.name}.blob.${az.environment().suffixes.storage}/${keysContainerName}/${keysBlobName}'
            }
            {
              name: 'Authentication__KeysKeyVaultKeyUri'
              value: keysKey.properties.keyUri
            }
          ]
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

// Managed certificate with CNAME domain control validation. Second stage only:
// the hostname must already be attached to the app (bindCustomDomain run) and
// the CNAME plus asuid TXT records must exist (see
// infrastructure/archive/README.md); otherwise issuance fails. No explicit
// dependsOn: the app references the certificate once bound, so an explicit
// back-reference would be circular; the persisted hostname from the first
// stage satisfies the issuance prerequisite.
resource certificate 'Microsoft.App/managedEnvironments/managedCertificates@2024-03-01' = if (bindCustomDomain && bindManagedCertificate) {
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
output runtimeIdentityClientId string = runtimeIdentity.properties.clientId
output keysBlobUri string = 'https://${storage.name}.blob.${az.environment().suffixes.storage}/${keysContainerName}/${keysBlobName}'
output keysKeyVaultKeyUri string = keysKey.properties.keyUri
output customDomainBound bool = bindCustomDomain
