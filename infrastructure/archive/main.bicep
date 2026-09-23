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

@description('Maintenance mode flag (ARC-012). True pauses member API access with a German maintenance state. The release workflow toggles the live value around migrations; the infrastructure workflow preserves it on re-runs, so an infrastructure change cannot silently reopen the window.')
param maintenanceMode bool = false

@description('Archive storage account name for the private Data Protection key ring (ARC-011). Lowercase alphanumeric, 3-24 chars, globally unique.')
param storageAccountName string = 'stliedertafelarchive'

@description('Private blob container holding the Data Protection key ring.')
param keysContainerName string = 'dataprotection'

@description('Blob object name for the Data Protection key ring inside the container.')
param keysBlobName string = 'keys.xml'

@description('Private blob container for member files (ARC-049). Default matches the backend AssetStorageOptions container name.')
param assetsContainerName string = 'archive-assets'

@description('Key Vault RSA wrapping-key name protecting the Data Protection key ring (ARC-011).')
param keysKeyName string = 'dataprotection-wrap'

@description('Azure region for the Azure OpenAI account and its children. ARC-021: Austria East is not an EU Data Zone region for Azure OpenAI, so chat resources live in Germany West Central while everything else stays in `location` (EU Data Zone gate).')
param aiLocation string = 'germanywestcentral'

@description('Azure OpenAI (Microsoft Foundry) account name. Lowercase alphanumeric and hyphens, globally unique; also the custom subdomain.')
param aiAccountName string = 'aoai-liedertafel-archive'

@description('Foundry project name under the AI account.')
param aiProjectName string = 'liedertafel-archive'

@description('Azure OpenAI chat deployment name. The host app configuration references the model by this name.')
param aiChatDeploymentName string = 'gpt-5-4-mini'

@description('Group budget anchor: first day of the current month. utcNow may only appear as a parameter default, so the resource derives the budget window from this value.')
param budgetMonthAnchor string = utcNow('yyyy-MM-01')

@description('Object id of the deploying release identity (resolved by the workflow). Empty in PR what-if; when set, the identity receives Cost Management Contributor so the group budget write is authorized.')
param deployPrincipalId string = ''

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

// ARC-049 — the browser PUTs upload blocks directly to Blob, so the app
// origins must be allowed. The backend's emulator bootstrap keeps a
// permissive rule for local Azurite; this is the hosted counterpart.
// Modify-only: the managed blob service always exists with a storage account.
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    cors: {
      corsRules: [
        {
          allowedOrigins: [
            'https://${customDomain}'
            'https://${app.properties.configuration.ingress.fqdn}'
          ]
          allowedMethods: [
            'GET'
            'HEAD'
            'PUT'
          ]
          allowedHeaders: ['*']
          exposedHeaders: ['ETag', 'x-ms-request-id']
          maxAgeInSeconds: 3600
        }
      ]
    }
  }
}

resource keysContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: keysContainerName
  properties: {
    publicAccess: 'None'
  }
}

// ARC-049 — private member-file container. The runtime identity's
// account-scoped Storage Blob Data Contributor assignment above already
// covers it, including user-delegation key generation for ticket signing.
resource assetsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: assetsContainerName
  properties: {
    publicAccess: 'None'
  }
}

// ARC-021 — Azure OpenAI (Microsoft Foundry) chat path. The account carries
// the stateless keyless inference surface: deployments live at account level,
// the model name never appears in a hostname, and `disableLocalAuth` forces
// the Entra path for the runtime identity, so no API key exists anywhere.
// The account lives in `aiLocation`, not the group's `location`: Austria East
// is not an EU Data Zone region for Azure OpenAI (ARC-021 gate), and the
// default chat model stays inside the EU Data Zone.
// Create/Modify only: the account, project and deployment are always-add,
// never destructive.
resource aoaiAccount 'Microsoft.CognitiveServices/accounts@2026-07-01' = {
  name: aiAccountName
  location: aiLocation
  sku: {
    name: 'S0'
  }
  kind: 'AIServices'
  properties: {
    // Hostname anchor for the inference endpoint env value below; also
    // requires unique DNS.
    customSubDomainName: aiAccountName
    // Keyless Entra-only: the runtime identity holds Cognitive Services
    // OpenAI User; no key material is issued to anyone.
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
    allowProjectManagement: true
  }
}

// Minimal Foundry project child (ARC-021). Inference happens at the
// account-level deployments; the project exists for future Foundry tooling
// only and introduces no separate endpoint or credentials.
resource aoaiProject 'Microsoft.CognitiveServices/accounts/projects@2026-07-01' = {
  parent: aoaiAccount
  name: aiProjectName
  location: aiLocation
  properties: {
    displayName: 'Liedertafel-Archiv'
    description: 'Foundry-Projekt für den Chat-Assistenten des Archivs.'
  }
}

// ARC-021 pinning rule: the deployment pins a dated model version; a change
// needs a re-run of the chat evaluation and a pricing re-check before the
// function name and version advance. 'OnceCurrentVersionExpired' keeps the
// pin in place until the provider expires that version — no silent upgrade
// while the pin is live, but the expiry transition itself is automatic and
// still needs that re-run (version expiry is an ARC-043 maintenance check).
// DataZoneStandard at 30k TPM is far above the ≤5-member app's needs and
// stays regional within the EU Data Zone.
// Inference endpoint: derived once from the unique custom subdomain and
// shared by the Container App env entry and the `aiEndpoint` output.
var aiEndpointUri = 'https://${aoaiAccount.properties.customSubDomainName}.openai.azure.com/'

resource aoaiChatDeployment 'Microsoft.CognitiveServices/accounts/deployments@2026-07-01' = {
  parent: aoaiAccount
  name: aiChatDeploymentName
  sku: {
    name: 'DataZoneStandard'
    capacity: 30
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-5.4-mini'
      version: '2026-03-17'
    }
    versionUpgradeOption: 'OnceCurrentVersionExpired'
  }
}

// Keyless chat data-plane access (ARC-021): `disableLocalAuth` means the
// identity assignment is the only path in. Skipped in PR what-if like the
// other RBAC assignments; the deployment name travels via the app env below.
resource aoaiOpenAIUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployRoleAssignments) {
  name: guid(aoaiAccount.id, runtimeIdentity.id, 'AoaiOpenAIUser')
  scope: aoaiAccount
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
    )
    principalId: runtimeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ARC-004 review trigger — group operating budget. The archive group target
// is EUR 10 per month. This budget is alert-only and never an automatic
// spending cap: the app-side ARC-021 semantics (EUR 5 alert plus manual
// disable) remain the primary circuit breaker, this group-wide budget is the
// backstop. The anchor parameter keeps `utcNow` in its allowed position so
// consecutive runs inside a month stay idempotent; the window rolls forward
// with the first deploy of each new month (a redeployed budget resets its
// period, matching the alert-only intent).
// The budget write needs Cost Management Contributor, which the release
// identity holds only after the assignment below (UAA lets the deploying
// OIDC app self-grant). dependsOn keeps the order; RBAC propagation can
// still lag, so a failed budget PUT on the first run is retried by a rerun.
resource cmContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployPrincipalId)) {
  name: guid(resourceGroup().id, 'cm-contributor', deployPrincipalId)
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '434105ed-43f6-45c7-a02f-909b2ba83430'
    )
    principalId: deployPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource budget 'Microsoft.Consumption/budgets@2019-10-01' = {
  name: 'budget-liedertafel-archive'
  dependsOn: [cmContributor]
  properties: {
    category: 'Cost'
    amount: 10
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: budgetMonthAnchor
      endDate: dateTimeAdd(budgetMonthAnchor, 'P12M')
    }
    // No filter: the budget watches the whole resource group on purpose
    // (group backstop, not an app-scoped counter).
    notifications: {
      Actual_80: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 80
        thresholdType: 'Actual'
        contactEmails: [
          'j.wegenschimmel@gmail.com'
        ]
        contactRoles: [
          'Owner'
        ]
        locale: 'en-us'
      }
      Actual_100: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 100
        thresholdType: 'Actual'
        contactEmails: [
          'j.wegenschimmel@gmail.com'
        ]
        contactRoles: [
          'Owner'
        ]
        locale: 'en-us'
      }
      Forecast_100: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 100
        thresholdType: 'Forecast'
        contactEmails: [
          'j.wegenschimmel@gmail.com'
        ]
        contactRoles: [
          'Owner'
        ]
        locale: 'en-us'
      }
    }
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
            // ARC-049: Entra-only assets account endpoint. The backend signs
            // user-delegation SAS tickets through the runtime identity (no
            // storage key or connection string reaches the app).
            {
              name: 'Archive__Assets__ServiceUri'
              value: 'https://${storage.name}.blob.${az.environment().suffixes.storage}'
            }
            // ARC-011-1: stable WebAuthn relying-party ID and the exact
            // trusted ceremony origins. Never derived from request host
            // headers; the startup guard fails without the RP ID outside
            // Development.
            {
              name: 'Authentication__PasskeyRelyingPartyId'
              value: customDomain
            }
            {
              name: 'Authentication__PasskeyOrigins__0'
              value: 'https://${customDomain}'
            }
            // ARC-012: shared maintenance state, declared in topology so it
            // is visible and preserved. The release workflow toggles the
            // live value with az containerapp update --set-env-vars around
            // migrations; the infrastructure workflow passes the live value
            // through (like the image) instead of resetting it.
            {
              name: 'Archive__MaintenanceMode'
              value: maintenanceMode ? 'true' : 'false'
            }
            // ARC-021: maintained chat path, keyless via the runtime identity
            // (Cognitive Services OpenAI User on the account; no API key).
            // Chat is enabled from the first deploy by maintainer decision
            // 2026-09-23 (sole production user pre-release); the ARC-021
            // EUR 5 alert + manual-disable semantics stay unchanged. The
            // model is pinned to the dated deployment below the account, so
            // upgrades require the evaluation/pricing re-check, and the
            // endpoint builds from the account's unique custom subdomain.
            {
              name: 'Archive__Chat__Provider'
              value: 'AzureOpenAI'
            }
            {
              name: 'Archive__Chat__Endpoint'
              value: aiEndpointUri
            }
            {
              name: 'Archive__Chat__DeploymentName'
              value: aiChatDeploymentName
            }
            {
              name: 'Archive__Chat__Enabled'
              value: 'true'
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
// ARC-049: assets account endpoint for extraction/import jobs; they receive
// their own role assignment separately from the key-ring identity state.
output assetsServiceUri string = 'https://${storage.name}.blob.${az.environment().suffixes.storage}'
// ARC-021: Azure OpenAI inference endpoint (custom subdomain of the AI
// account), same value the app receives as `Archive__Chat__Endpoint`.
output aiEndpoint string = aiEndpointUri
output customDomainBound bool = bindCustomDomain
