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
param bindCustomDomain = true
param bindManagedCertificate = true
param logAnalyticsDailyCapGb = 1

// ARC-042: maintainer destination addresses (primary first, then the second
// maintainer). Not secrets, but they appear in deployment diffs/portal — set
// them here only. While this list is empty, every alert deploys disabled.
// param maintainerAlertEmails = ["...@liedertafel-mining.at", "...@example.org"]
param deployRoleAssignments = true
// ARC-043 (#150): comma-separated EUR thresholds for the AI spend notices
// before the EUR 15 hard cap (app + reading sweep). Deliberately empty in
// the committed file: the maintainers pick the numbers (suggestion pending:
// '5,12') and keep them here; empty keeps every notice disabled.
// param aiNoticeThresholdsEur = '5,12'
// ARC-012: declared default only. Workflows override this per run with the
// live value (infra) or the release window state (release), so neither a
// fresh deploy nor an infra re-run silently reopens member access.
param maintenanceMode = false
