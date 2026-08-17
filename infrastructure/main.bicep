targetScope = 'resourceGroup'

@description('Name of the static web app to adopt.')
param siteName string

@description('Region of the static web app.')
param staticSitesLocation string

@description('Custom domains for the site.')
param customDomains array = []

@description('Name of the internal dashboard static web app.')
param dashboardSiteName string

@description('Custom domains for the dashboard site.')
param dashboardCustomDomains array = []

@description('Name of the storage account for website analytics.')
param storageAccountName string

module storage './modules/storage.bicep' = {
  name: 'storage'
  params: {
    storageAccountName: storageAccountName
    location: staticSitesLocation
  }
}

module staticSites './modules/static-sites.bicep' = {
  name: 'staticSites'
  params: {
    siteName: siteName
    location: staticSitesLocation
    customDomains: customDomains
    dashboardSiteName: dashboardSiteName
    dashboardCustomDomains: dashboardCustomDomains
  }
}