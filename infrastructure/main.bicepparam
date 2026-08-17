using './main.bicep'

param staticSitesLocation = 'westeurope'
param siteName = 'liedertafel'
param storageAccountName = 'stliedertafel'
param customDomains = [
  'liedertafel-mining.at'
  'www.liedertafel-mining.at'
]