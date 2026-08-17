using './main.bicep'

param staticSitesLocation = 'westeurope'
param siteName = 'liedertafel'
param storageAccountName = 'stliedertafel'
param customDomains = [
  'liedertafel-mining.at'
  'www.liedertafel-mining.at'
]
param dashboardSiteName = 'liedertafel-dashboard'
param dashboardCustomDomains = [
  'dashboard.liedertafel-mining.at'
]