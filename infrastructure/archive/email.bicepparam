using './email.bicep'

// Non-secret defaults for the ARC-010 send-only mail path. The runtime
// identity object ID (principalId of id-archive-app, not its clientId) is
// injected per run so preview what-if can pass empty and skip the role
// assignment while real deploys grant the send permission.
param runtimePrincipalId = readEnvironmentVariable('ARCHIVE_RUNTIME_PRINCIPAL_ID')
param emailServiceName = 'liedertafel-archive'
param communicationServiceName = 'acs-liedertafel-archive'
param senderDomain = 'liedertafel-mining.at'
param senderUsername = 'archiv'
param senderDisplayName = 'Liedertafel Archiv'
param dataLocation = 'Europe'
param deployRoleAssignments = true
// Stage 2 (2026-09-17): domain verified (Domain/SPF/DKIM/DKIM2), link it to
// the Communication Service.
param linkDomain = true
