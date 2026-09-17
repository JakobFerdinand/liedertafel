// ARC-010 — Azure Communication Services Email for real German sign-in mail.
// Resource-group scope, deployed to RG-Liedertafel-Archive.
//
// Scope notes:
// - This template owns the send-only mail path: Email Service (Europe data
//   location), the verified custom sender domain liedertafel-mining.at with
//   sender username archiv, the linked Communication Services resource (Europe
//   data location), and the runtime managed-identity send permission.
// - Domain verification itself is out of band: after deployment, enter the
//   generated TXT/SPF/DKIM records (see outputs) at World4You, then run
//   initiate-verification (see infrastructure/archive/README.md). No MX
//   record is issued or required: the sender is send-only by maintainer
//   decision. No mailbox, forwarding service, or receiving path exists.
// - Initial sending quota is subscription-wide (30/min, 100/hour for custom
//   domains) and shared with the unrelated alpakasoelde sender; it is not a
//   per-resource setting. API acceptance (Succeeded) never promises inbox
//   delivery; per-recipient delivery needs Event Grid/Monitor, not this call.
targetScope = 'resourceGroup'

@description('Email Service resource name (ARC-004 contract).')
param emailServiceName string = 'liedertafel-archive'

@description('Linked Communication Services resource name (ARC-004 contract).')
param communicationServiceName string = 'acs-liedertafel-archive'

@description('Verified custom sender domain (apex). Send-only: no MX record is issued or required.')
param senderDomain string = 'liedertafel-mining.at'

@description('Sender username on the verified domain. Full sender: archiv@liedertafel-mining.at.')
param senderUsername string = 'archiv'

@description('Display name shown on German sign-in and invitation mail.')
param senderDisplayName string = 'Liedertafel Archiv'

@description('Data location for both the email and communication resources (ARC-004/010 contract: Europe).')
param dataLocation string = 'Europe'

@description('Object ID (principalId, not clientId) of the runtime user-assigned managed identity id-archive-app. Empty skips the assignment (preview runs).')
param runtimePrincipalId string = ''

@description('Deploy the RBAC role assignment. PR what-if uses a Contributor-only preview identity without Microsoft.Authorization/roleAssignments/write, so it previews with an empty principal ID; real deploys pass the identity object ID.')
param deployRoleAssignments bool = true

@description('Link the verified domain to the Communication Service. Two stages like the ARC-009 certificate: deploy first with false (verification needs DNS first; linking an unverified domain fails with DomainValidationError), then flip to true after World4You entry plus initiate-verification.')
param linkDomain bool = false

resource emailService 'Microsoft.Communication/emailServices@2023-04-01' = {
  name: emailServiceName
  location: 'global'
  properties: {
    dataLocation: dataLocation
  }
}

resource domain 'Microsoft.Communication/emailServices/domains@2023-04-01' = {
  parent: emailService
  name: senderDomain
  location: 'global'
  properties: {
    domainManagement: 'CustomerManaged'
    userEngagementTracking: 'Disabled'
  }
}

resource sender 'Microsoft.Communication/emailServices/domains/senderUsernames@2023-04-01' = {
  parent: domain
  name: senderUsername
  properties: {
    username: senderUsername
    displayName: senderDisplayName
  }
}

resource communicationService 'Microsoft.Communication/communicationServices@2023-04-01' = {
  name: communicationServiceName
  location: 'global'
  properties: {
    dataLocation: dataLocation
    linkedDomains: linkDomain ? [
      domain.id
    ] : []
  }
}

// ARC-004 contract: the runtime identity sends mail keyless via
// EmailClient(endpoint, DefaultAzureCredential). Built-in role
// "Communication and Email Service Owner" (verified live in the tenant on
// 2026-09-17), assigned directly to the managed identity: group-nested
// assignment is a known ACS limitation, so the identity object ID is passed
// explicitly. Skipped in PR what-if by design (see param docs).
resource mailSendPermission 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployRoleAssignments && !empty(runtimePrincipalId)) {
  name: guid(communicationService.id, runtimePrincipalId, 'CommEmailOwner')
  scope: communicationService
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '09976791-48a7-449e-bb21-39d1a415f350'
    )
    principalId: runtimePrincipalId
    principalType: 'ServicePrincipal'
  }
}

@description('Full sender address used by the backend (Auth:MailFrom default).')
output senderAddress string = '${senderUsername}@${senderDomain}'

@description('Communication Services endpoint for EmailClient(Uri, DefaultAzureCredential). Europe geography regionalizes the host (verified live); do not use the unqualified .communication.azure.com form.')
output communicationEndpoint string = 'https://${communicationServiceName}.europe.communication.azure.com'

@description('Generated DNS records for World4You entry (ownership TXT, SPF, DKIM/DKIM2 CNAMEs). Read-only; supplied by Azure at creation.')
output verificationRecords object = domain.properties.verificationRecords

@description('Per-record verification state (Domain/SPF/DKIM/DKIM2). Verification is triggered out of band after DNS entry.')
output verificationStates object = domain.properties.verificationStates
