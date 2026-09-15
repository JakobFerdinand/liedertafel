// ARC-009 — archive resource group bootstrap (subscription scope).
// Creates RG-Liedertafel-Archive only; all workload resources live in
// main.bicep (resource-group scope). Idempotent: safe to re-run.
targetScope = 'subscription'

@description('Dedicated archive resource group.')
param archiveResourceGroupName string = 'RG-Liedertafel-Archive'

@description('Group metadata location. Workload resources use the location parameter in main.bicepparam.')
param resourceGroupLocation string = 'austriaeast'

resource archiveGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: archiveResourceGroupName
  location: resourceGroupLocation
}

output archiveResourceGroupId string = archiveGroup.id
