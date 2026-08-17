targetScope = 'resourceGroup'

@description('Name of the static web app to adopt.')
param siteName string

@description('Region of the static web app.')
param staticSitesLocation string

@description('Custom domains for the site.')
param customDomains array = []

module staticSites './modules/static-sites.bicep' = {
  name: 'staticSites'
  params: {
    siteName: siteName
    location: staticSitesLocation
    customDomains: customDomains
  }
}