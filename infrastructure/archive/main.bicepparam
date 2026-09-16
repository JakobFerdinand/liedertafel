using './main.bicep'

// Non-secret defaults for the archive shell. containerImage (immutable digest)
// and ghcrPassword are required with no defaults: they are read from
// environment variables so Bicep validates the params file before Azure CLI
// overrides would apply (BCP258). Workflows set ARCHIVE_CONTAINER_IMAGE and
// ARCHIVE_GHCR_PASSWORD per run, preserving the current release and keeping
// secrets out of the repo. Missing variables fail the deployment explicitly.
param containerImage = readEnvironmentVariable('ARCHIVE_CONTAINER_IMAGE')
param ghcrPassword = readEnvironmentVariable('ARCHIVE_GHCR_PASSWORD')
param location = 'austriaeast'
param environmentName = 'cae-liedertafel-archive'
param appName = 'ca-liedertafel-archive'
param runtimeIdentityName = 'id-archive-app'
param vaultName = 'kv-liedertafel-archive'
param workspaceName = 'log-liedertafel-archive'
param ghcrUsername = 'jakobferdinand'
param customDomain = 'archiv.liedertafel-mining.at'
param bindCustomDomain = false
param logAnalyticsDailyCapGb = 1
param deployRoleAssignments = true
