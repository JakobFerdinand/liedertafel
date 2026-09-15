using './main.bicep'

// Non-secret defaults for the archive shell. containerImage (immutable digest)
// and ghcrPassword are injected per-run by the workflows so infrastructure
// re-runs preserve the current release and secrets never land in the repo.
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
