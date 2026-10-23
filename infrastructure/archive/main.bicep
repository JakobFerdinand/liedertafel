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
//   infrastructure re-run cannot revert a release. The app and the
//   extraction job (ARC-034) and the AI reading jobs (ARC-034-1) run the
//   same image parameter.
// - No Neon or email resources here. The storage account below is the only
//   storage integration in this template and now carries the extraction
//   queue (ARC-034) and the reading queue (ARC-034-1) next to the key-ring
//   and member-file blobs; both queues are declared in this template.
//   Hosted sign-in is ARC-011, real email ARC-010.
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

@description('Extraction job identity name (ARC-034). Least privilege: queue contributor plus blob reader on the assets storage account, nothing else.')
param extractionIdentityName string = 'id-archive-extract'

@description('Queue-triggered extraction job name (ARC-034). Runs the same backend image with --extract-queue as a finite run.')
param extractionJobName string = 'ja-archive-extract'

@description('AI reading job identity name (ARC-034-1). The ONLY job identity holding the Azure OpenAI user role; also queue contributor plus blob reader on the assets storage account, nothing else.')
param readingIdentityName string = 'id-archive-read'

@description('Queue-triggered AI reading job name (ARC-034-1). Runs the same backend image with --read-queue as a finite run.')
param readingJobName string = 'ja-archive-read'

@description('Identity of the scheduled reading dispatch sweep (ARC-034-1). Queue contributor on the assets storage account only: no blob, no Azure OpenAI role.')
param readingDispatchIdentityName string = 'id-archive-read-dispatch'

@description('Scheduled reading dispatch job name (ARC-034-1). Runs the same backend image with --dispatch-reading, which wakes work parked for budget.')
param readingDispatchJobName string = 'ja-archive-read-dispatch'

@description('Scheduled Papierkorb cleanup job name (#146). Runs the same backend image with --cleanup-papierkorb; removes trashed rows past the seven-day retention and releases their blobs.')
param papierkorbCleanupJobName string = 'ja-archive-papierkorb'

@description('Cron expression (UTC) of the Papierkorb cleanup. Daily is enough: retention is seven days, so nothing expires faster than that.')
param papierkorbCleanupCron string = '43 2 * * *'

@description('Cron expression (UTC) of the reading dispatch sweep. Daily is enough: parked work only has to wake when a new month starts or the cap is raised.')
param readingDispatchCron string = '17 3 * * *'

@description('Archive:Reading:Enabled (ARC-034-1). True lets the extraction job hand scores to the AI reading job and shows the reading to editors. Set false (and redeploy) to switch the feature off again: nothing is handed over, the editor actions answer 409 and the pages hide the reading.')
param readingEnabled bool = true

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


@description('Azure OpenAI chat deployment name. The host app configuration references the model by this name, and `Archive:Ai:Models` must price exactly this key (a test keeps the two in step). ARC-021-1: GPT-6 Luna.')
param aiChatDeploymentName string = 'gpt-6-luna'


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

// ARC-034 — least-privilege identity of the queue-triggered extraction job.
// It only reads the assets account's blobs (bounded OpenReadAsync) and
// creates/sends/receives/processes queue messages; it deliberately holds no
// vault, key-ring, mail or chat roles.
resource extractionIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: extractionIdentityName
  location: location
}

// ARC-034-1 — identity of the queue-triggered AI reading job. It is the only
// job identity that may call the model: queue contributor and blob reader on
// the assets account plus the Azure OpenAI user role on the AI account. It
// holds no vault, key-ring, mail or write-to-blob roles.
resource readingIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: readingIdentityName
  location: location
}

// ARC-034-1 — identity of the scheduled dispatch sweep. The sweep only reads
// and updates `reading_jobs` rows and sends queue messages (it asks the
// budget ledger in the database, it never calls the model), so it gets the
// queue role alone: no blob access and no Azure OpenAI role.
resource readingDispatchIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: readingDispatchIdentityName
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

// ARC-034: the extraction queue lives on the SAME assets storage account —
// one account owns blobs and queues. KEDA polls only this queue; the finite
// worker drains it and exits, so idle deployments never query the database.
// The queue is declared here: KEDA only starts the job once it can read the
// queue, and the app's Message Sender role cannot create it, so a
// worker-provisioned queue would never come into existence on a fresh
// account. The worker's CreateIfNotExists stays as a harmless no-op.
// The name must match ExtractionOptions.QueueName.
var extractionQueueName = 'archive-extraction'

resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource extractionQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queueService
  name: extractionQueueName
}

var extractionQueueUri = 'https://${storage.name}.queue.${az.environment().suffixes.storage}'

// ARC-034-1: the AI reading queue, same account and same reasoning as the
// extraction queue (declared here so KEDA can poll it before any worker
// ran). The name must match ReadingOptions.DefaultQueueName. Reading code
// falls back to the extraction queue service URI (same account), so the app
// and the extraction job (which holds queue contributor on the account, and
// sends the hand-over message) need no extra setting.
var readingQueueName = 'archive-reading'

resource readingQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queueService
  name: readingQueueName
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
  // System-assigned identity is a Foundry requirement for the project child
  // ("To create projects, you must enable a managed identity on your
  // resource"); the runtime identity keeps its explicit role assignment.
  identity: {
    type: 'SystemAssigned'
  }
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

// ARC-021 pinning rule: the deployment pins a dated model version; a change
// needs a re-run of the chat evaluation and a pricing re-check before the
// function name and version advance. 'OnceCurrentVersionExpired' keeps the
// pin in place until the provider expires that version — no silent upgrade
// while the pin is live, but the expiry transition itself is automatic and
// still needs that re-run (version expiry is an ARC-043 maintenance check).
// ARC-021-1: GPT-6 Luna (`gpt-6-luna`, version 2026-09-22, Microsoft Foundry
// model catalogue), the one model for all AI work. The deployment is a NEW
// resource named after the model; the former `gpt-5-4-mini` deployment is no
// longer declared but is never deleted by this template (incremental mode), so
// the what-if stays Create-only and the old deployment remains a rollback path
// until the maintainer deletes it after the hosted verification.
// DataZoneStandard stays inside the EU Data Zone. Capacity 30 = 30k tokens per
// minute: a burst limiter behind the application's EUR 15 monthly cap, not a
// spending control by itself. At the EU Data Zone rates (0.1056 / 0.528 EUR per
// million input / output tokens) a saturated minute costs at most ~0.016 EUR,
// so a runaway loop is bounded to about 1 EUR per hour while the application
// ledger refuses every call beyond the cap. Far above the <=5-member app's needs.
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
      name: 'gpt-6-luna'
      version: '2026-09-22'
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

// ARC-034: the runtime identity dispatches extraction envelopes inline at
// finalize (best-effort; a failure keeps the row un-enqueued for the
// dispatch sweep). Message Sender only — add a message, nothing else; the
// sweep jobs own receive/process. Skipped in PR what-if like the other RBAC
// assignments.
resource queueMessageSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployRoleAssignments) {
  name: guid(storage.id, runtimeIdentity.id, 'queue-message-sender')
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'c6a89b2d-59bc-44d0-9896-0f6e12d7b80a'
    )
    principalId: runtimeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ARC-034: extraction job identity on the same account. Queue Data
// Contributor (create + send + receive + process): the worker self-
// provisions the queue, the dispatch sweep sends, the drain receives,
// processes and deletes — and KEDA's queue-length poll needs the
// `queues/read` action, which the narrower Message Sender/Processor roles
// lack, so no split of narrow roles covers this job. Skipped in PR what-if
// like the other RBAC assignments.
resource extractionQueueContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployRoleAssignments) {
  name: guid(storage.id, extractionIdentity.id, 'queue-data-contributor')
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
    )
    principalId: extractionIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ARC-034: bounded PDF reads for the extraction worker (OpenReadAsync on
// the private assets container). Reader only — the app keeps its existing
// blob-contributor assignment for uploads and ticket signing. No vault,
// key-ring or chat roles for the job identity. Skipped in PR what-if like
// the other RBAC assignments.
resource extractionBlobReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployRoleAssignments) {
  name: guid(storage.id, extractionIdentity.id, 'blob-data-reader')
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
    )
    principalId: extractionIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ARC-034-1: AI reading job identity. Same three scopes as the extraction
// job, plus the model: Queue Data Contributor (receive/process/delete, the
// worker's self-provisioning and the sweep's send, and KEDA's `queues/read`
// poll), Blob Data Reader (bounded PDF reads) and Azure OpenAI user on the
// AI account. Skipped in PR what-if like the other RBAC assignments.
resource readingQueueContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployRoleAssignments) {
  name: guid(storage.id, readingIdentity.id, 'queue-data-contributor')
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
    )
    principalId: readingIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource readingBlobReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployRoleAssignments) {
  name: guid(storage.id, readingIdentity.id, 'blob-data-reader')
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
    )
    principalId: readingIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Cognitive Services OpenAI User, declared exactly like the runtime
// identity's `aoaiOpenAIUser`. The reading job is the only job identity that
// holds it; the extraction job and the dispatch sweep stay model-free.
resource readingOpenAIUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployRoleAssignments) {
  name: guid(aoaiAccount.id, readingIdentity.id, 'AoaiOpenAIUser')
  scope: aoaiAccount
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
    )
    principalId: readingIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ARC-034-1: dispatch sweep identity. The sweep creates the queue when
// missing (a CreateIfNotExists call needs the write action, which Message
// Sender lacks) and sends, so Queue Data Contributor is the narrowest
// built-in role that covers it. Skipped in PR what-if.
resource readingDispatchQueueContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployRoleAssignments) {
  name: guid(storage.id, readingDispatchIdentity.id, 'queue-data-contributor')
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
    )
    principalId: readingDispatchIdentity.properties.principalId
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
            // ARC-034: finalize dispatches the extraction envelope inline
            // (best-effort; a failure keeps the row un-enqueued for the
            // dispatch sweep). The runtime identity holds Storage Queue
            // Data Message Sender on the same account — no receive or
            // process rights, and no storage key.
            {
              name: 'Archive__Extraction__QueueServiceUri'
              value: extractionQueueUri
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
            // 2026-09-23 (sole production user pre-release). Spending is held
            // by the application's hard EUR 15 monthly cap (ARC-022-3);
            // Archive__Chat__Disabled=true stays the manual kill switch. The
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
              // The deployment's own name: the app revision that points at
              // Luna is only created after the deployment exists.
              value: aoaiChatDeployment.name
            }
            {
              name: 'Archive__Chat__Enabled'
              value: 'true'
            }
            // ARC-034-1: reading is off in appsettings.json until the reading
            // job, its identity and the dispatch sweep exist; this template
            // declares them, so the switch follows `readingEnabled`.
            {
              name: 'Archive__Reading__Enabled'
              value: readingEnabled ? 'true' : 'false'
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

// ARC-034 — queue-triggered extraction job. It runs the SAME backend image
// as the app (the release workflow pins both to the same digest, and the
// infra workflow passes the currently deployed image through for both), and
// each execution is one finite worker run (`--extract-queue`): it dispatches
// un-enqueued rows, drains the extraction queue and exits when a receive
// round comes back empty. KEDA polls ONLY the queue — never Neon — and
// minExecutions 0 keeps an idle deployment execution-free. Platform retries
// stay off (`replicaRetryLimit` 0): retries are the worker's own bounded
// attempt ladder. The 30-minute replica timeout is generous headroom over
// the worker's 10-minute drain budget, and the 1 Gi memory cap bounds a
// runaway parse end-to-end (OOM kill resolves through the stale-lease
// ladder). The extraction identity's Storage Queue Data Contributor covers
// create (a no-op safeguard; Bicep declares the queue), send, receive and
// process.
// The scale rule authenticates via the `identity` property — the current
// Container Apps guidance (managed-identity#scale-rules: "use the identity
// property instead of the auth property") with a user-assigned identity
// resource ID — which pins KEDA's queue poll to `id-archive-extract` even
// though the job carries a second identity for Key Vault secret resolution.
// API version 2025-01-01 is the oldest GA version whose JobScaleRule
// carries that `identity` property (2024-03-01 offers only secret-based
// ScaleRuleAuth with ambiguous multi-identity semantics).
resource extractJob 'Microsoft.App/jobs@2025-01-01' = {
  name: extractionJobName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${extractionIdentity.id}': {}
      // Attached only so the platform can resolve the Key Vault-referenced
      // database secret below; identitySettings lifecycle None keeps it
      // unavailable inside the container (least privilege, per the
      // Container Apps "control managed identity availability" guidance —
      // `None` exists exactly for identities used only for scale rules or
      // Key Vault secrets).
      '${runtimeIdentity.id}': {}
    }
  }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      identitySettings: [
        {
          identity: runtimeIdentity.id
          lifecycle: 'None'
        }
        {
          identity: extractionIdentity.id
          lifecycle: 'All'
        }
      ]
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
        // Same pattern as the app: the Neon runtime connection arrives as a
        // Key Vault reference resolved by the runtime identity (whose
        // Secrets User role is reused; the job gains no new vault rights).
        {
          name: 'archive-db-connection'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/archive-db-connection'
          identity: runtimeIdentity.id
        }
      ]
      replicaTimeout: 1800
      replicaRetryLimit: 0
      // Per the Jobs REST schema, triggerType sits on the configuration and
      // selects the eventTriggerConfig variant (Bicep's type definition
      // warns otherwise, which is a known type inaccuracy for jobs).
      triggerType: 'Event'
      eventTriggerConfig: {
        replicaCompletionCount: 1
        parallelism: 1
        scale: {
          minExecutions: 0
          maxExecutions: 10
          pollingInterval: 30
          rules: [
            {
              name: 'extraction-queue'
              type: 'azure-queue'
              // The scaler endpoint derives from accountName for the public
              // cloud; the account is Entra-only (no shared key), so the
              // rule uses the extraction identity below. queueName must
              // match ExtractionOptions.QueueName — both default to
              // `archive-extraction`, and no env entry sets another value.
              metadata: {
                queueName: extractionQueue.name
                queueLength: '1'
                // KEDA's supported value is visibleonly (not visible): hidden
                // retry/lease messages must not activate an idle job.
                queueLengthStrategy: 'visibleonly'
                accountName: storage.name
              }
              identity: extractionIdentity.id
            }
          ]
        }
      }
    }
    template: {
      containers: [
        {
          name: 'extract'
          image: containerImage
          // The image's entrypoint stays `dotnet Archive.Backend.dll`; the
          // finite command selects the extraction worker path.
          args: [
            '--extract-queue'
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            {
              name: 'ConnectionStrings__archive-db'
              secretRef: 'archive-db-connection'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: extractionIdentity.properties.clientId
            }
            {
              name: 'Archive__Assets__ServiceUri'
              value: 'https://${storage.name}.blob.${az.environment().suffixes.storage}'
            }
            {
              name: 'Archive__Extraction__QueueServiceUri'
              value: extractionQueueUri
            }
            // Shared with the app; infra preserves the live flag and release
            // sets it before stopping executions for the migration window.
            {
              name: 'Archive__MaintenanceMode'
              value: maintenanceMode ? 'true' : 'false'
            }
            // ARC-034-1: the hand-over to the reading queue is made here, so
            // the extraction job needs the same switch as the app. The queue
            // URI falls back to Archive__Extraction__QueueServiceUri.
            {
              name: 'Archive__Reading__Enabled'
              value: readingEnabled ? 'true' : 'false'
            }
          ]
        }
      ]
    }
  }
}

// ARC-034-1 — queue-triggered AI reading job. Same shape as the extraction
// job (same image parameter, finite run, event trigger on a visible-only
// azure-queue rule authenticated through the job's own identity, Key Vault
// database secret resolved by the runtime identity that is attached with
// lifecycle None), with these differences:
// - it runs `--read-queue`, which first sweeps (wakes parked work, hands
//   un-enqueued rows over) and then drains the `archive-reading` queue;
// - its identity is the only job identity with the Azure OpenAI user role,
//   and the chat provider settings below point it at the pinned deployment
//   (the deployment name is the model key priced under Archive:Ai:Models);
// - memory 2 Gi: the PDF (up to 32 MiB) is read into memory and rendered by
//   native PDFium/Skia; an OOM kill resolves through the stale-lease ladder;
// - replicaTimeout 3000 s (50 min): the drain loop checks its 30 minute
//   drain budget only before it starts a message, and a message gets
//   min(8 minutes, the 20 minute receive visibility of its batch), not the
//   drain deadline. Worst case: cold start and sweep, 30 minutes of drain,
//   one last 8 minute message started just before the budget ran out, then
//   the OTLP flush: about 40 minutes, so 50 leaves headroom.
//   Platform retries stay off like the extraction job's: retries are the
//   worker's own bounded attempt ladder.
// Queue length 1 per execution, at most ONE execution at a time: reading is
// paid model work behind the EUR 15 cap, and the deployment has a 30k
// tokens-per-minute limit that one ten-image call already uses most of, so
// parallel runs (or a run next to member chat) would only cause 429s and
// spurious failed readings. One run still handles many messages in a row.
resource readJob 'Microsoft.App/jobs@2025-01-01' = {
  name: readingJobName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${readingIdentity.id}': {}
      // Resolves the Key Vault-referenced database secret only; lifecycle
      // None keeps it (and its blob-contributor/key-ring rights) unavailable
      // inside the container, exactly like the extraction job.
      '${runtimeIdentity.id}': {}
    }
  }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      identitySettings: [
        {
          identity: runtimeIdentity.id
          lifecycle: 'None'
        }
        {
          identity: readingIdentity.id
          lifecycle: 'All'
        }
      ]
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
        {
          name: 'archive-db-connection'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/archive-db-connection'
          identity: runtimeIdentity.id
        }
      ]
      replicaTimeout: 3000
      replicaRetryLimit: 0
      triggerType: 'Event'
      eventTriggerConfig: {
        replicaCompletionCount: 1
        parallelism: 1
        scale: {
          minExecutions: 0
          maxExecutions: 1
          pollingInterval: 30
          rules: [
            {
              name: 'reading-queue'
              type: 'azure-queue'
              // Same scaler contract as the extraction rule: Entra-only
              // account, so the rule authenticates as the reading identity;
              // queueName must match ReadingOptions.DefaultQueueName.
              metadata: {
                queueName: readingQueue.name
                queueLength: '1'
                queueLengthStrategy: 'visibleonly'
                accountName: storage.name
              }
              identity: readingIdentity.id
            }
          ]
        }
      }
    }
    template: {
      containers: [
        {
          name: 'read'
          image: containerImage
          args: [
            '--read-queue'
          ]
          resources: {
            cpu: json('1')
            memory: '2Gi'
          }
          env: [
            {
              name: 'ConnectionStrings__archive-db'
              secretRef: 'archive-db-connection'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: readingIdentity.properties.clientId
            }
            {
              name: 'Archive__Assets__ServiceUri'
              value: 'https://${storage.name}.blob.${az.environment().suffixes.storage}'
            }
            {
              name: 'Archive__Reading__QueueServiceUri'
              value: extractionQueueUri
            }
            {
              name: 'Archive__Reading__Enabled'
              value: readingEnabled ? 'true' : 'false'
            }
            // Same model settings as the app (keyless via AZURE_CLIENT_ID,
            // the reading identity holds the OpenAI user role).
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
              value: aoaiChatDeployment.name
            }
            {
              name: 'Archive__MaintenanceMode'
              value: maintenanceMode ? 'true' : 'false'
            }
          ]
        }
      ]
    }
  }
}

// ARC-034-1 — scheduled reading dispatch sweep. Rows parked for budget
// (`WaitingForBudget`) have no queue message, so nothing would start the
// reading job for them when a new month begins or the cap is raised; this
// daily run of `--dispatch-reading` wakes them (and hands un-enqueued rows
// to the queue). It runs the same image parameter, but with its own minimal
// identity (queue contributor only). It never calls the model — the budget
// check reads the ledger in the database — but it prices the worst case
// against the configured deployment name, so it carries the same
// provider/endpoint/deployment settings as the reading job (configuration,
// not rights; without them the model key would not match a price and no
// parked row would ever wake). Maintenance mode makes a run exit before
// touching the database.
resource readDispatchJob 'Microsoft.App/jobs@2025-01-01' = {
  name: readingDispatchJobName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${readingDispatchIdentity.id}': {}
      '${runtimeIdentity.id}': {}
    }
  }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      identitySettings: [
        {
          identity: runtimeIdentity.id
          lifecycle: 'None'
        }
        {
          identity: readingDispatchIdentity.id
          lifecycle: 'All'
        }
      ]
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
        {
          name: 'archive-db-connection'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/archive-db-connection'
          identity: runtimeIdentity.id
        }
      ]
      replicaTimeout: 900
      replicaRetryLimit: 0
      triggerType: 'Schedule'
      scheduleTriggerConfig: {
        cronExpression: readingDispatchCron
        parallelism: 1
        replicaCompletionCount: 1
      }
    }
    template: {
      containers: [
        {
          name: 'read-dispatch'
          image: containerImage
          args: [
            '--dispatch-reading'
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            {
              name: 'ConnectionStrings__archive-db'
              secretRef: 'archive-db-connection'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: readingDispatchIdentity.properties.clientId
            }
            {
              name: 'Archive__Reading__QueueServiceUri'
              value: extractionQueueUri
            }
            {
              name: 'Archive__Reading__Enabled'
              value: readingEnabled ? 'true' : 'false'
            }
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
              value: aoaiChatDeployment.name
            }
            {
              name: 'Archive__MaintenanceMode'
              value: maintenanceMode ? 'true' : 'false'
            }
          ]
        }
      ]
    }
  }
}

// #146 — scheduled Papierkorb cleanup. Runs `--cleanup-papierkorb` daily:
// trashed Lieder, Auftritte and Dateien whose deletion is older than the
// seven-day retention are removed for good, their blobs released afterwards.
// It generates no model calls and never touches queues, so the runtime
// identity suffices: it reads the vault secret for the database connection
// and already holds Storage Blob Data Contributor on the assets account for
// the blob release. Maintenance mode makes the run exit before touching the
// database.
resource papierkorbCleanupJob 'Microsoft.App/jobs@2025-01-01' = {
  name: papierkorbCleanupJobName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${runtimeIdentity.id}': {}
    }
  }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      identitySettings: [
        {
          identity: runtimeIdentity.id
          lifecycle: 'All'
        }
      ]
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
        {
          name: 'archive-db-connection'
          keyVaultUrl: '${vault.properties.vaultUri}secrets/archive-db-connection'
          identity: runtimeIdentity.id
        }
      ]
      replicaTimeout: 900
      replicaRetryLimit: 0
      triggerType: 'Schedule'
      scheduleTriggerConfig: {
        cronExpression: papierkorbCleanupCron
        parallelism: 1
        replicaCompletionCount: 1
      }
    }
    template: {
      containers: [
        {
          name: 'papierkorb'
          image: containerImage
          args: [
            '--cleanup-papierkorb'
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: [
            {
              name: 'ConnectionStrings__archive-db'
              secretRef: 'archive-db-connection'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: runtimeIdentity.properties.clientId
            }
            {
              name: 'Archive__Assets__ServiceUri'
              value: 'https://${storage.name}.blob.${az.environment().suffixes.storage}'
            }
            {
              name: 'Archive__MaintenanceMode'
              value: maintenanceMode ? 'true' : 'false'
            }
          ]
        }
      ]
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
// ARC-034: extraction queue endpoint on the same assets account — the job
// receives it as `Archive__Extraction__QueueServiceUri`, and ARC-037's
// import job reuses this output, mirroring the ARC-051 assets pattern.
output assetsQueueServiceUri string = extractionQueueUri
// ARC-034-1: reading queue name on the same account (the reading code falls
// back to the extraction queue service URI for the endpoint).
output readingQueueName string = readingQueue.name
// ARC-021: Azure OpenAI inference endpoint (custom subdomain of the AI
// account), same value the app receives as `Archive__Chat__Endpoint`.
output aiEndpoint string = aiEndpointUri
output customDomainBound bool = bindCustomDomain
