@description('Name of the static web app to adopt.')
param siteName string

@description('Region of the static web app.')
param location string

@description('Custom domains for the site.')
param customDomains array = []

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

output siteId string = site.id