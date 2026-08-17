@description('Name of the static web app to adopt.')
param siteName string

@description('Region of the static web app.')
param location string

@description('Custom domains for the site.')
param customDomains array = []

@description('Name of the internal dashboard static web app.')
param dashboardSiteName string

@description('Custom domains for the dashboard site.')
param dashboardCustomDomains array = []

resource site 'Microsoft.Web/staticSites@2023-12-01' = {
  name: siteName
  location: location
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {
    allowConfigFileUpdates: true
  }
}

resource siteCustomDomain 'Microsoft.Web/staticSites/customDomains@2023-12-01' = [for domain in customDomains: {
  parent: site
  name: domain
}]

resource dashboardSite 'Microsoft.Web/staticSites@2023-12-01' = {
  name: dashboardSiteName
  location: location
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {
    allowConfigFileUpdates: true
  }
}

resource dashboardSiteCustomDomain 'Microsoft.Web/staticSites/customDomains@2023-12-01' = [for domain in dashboardCustomDomains: {
  parent: dashboardSite
  name: domain
}]

output siteId string = site.id

output dashboardSiteId string = dashboardSite.id