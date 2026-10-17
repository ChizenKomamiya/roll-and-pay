targetScope = 'resourceGroup'

param appName string = 'rollpay'
param staticWebAppLocation string = 'eastasia'
param tags object = {
  application: appName
  environment: 'prod'
  managedBy: 'bicep'
  purpose: 'demo'
}

// 本番と同じRGに置く公開デモ。API・DB・Entraの登録は追加しない。
resource demoSite 'Microsoft.Web/staticSites@2024-11-01' = {
  name: 'swa-${appName}-demo'
  location: staticWebAppLocation
  tags: tags
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {
    provider: 'Custom'
    stagingEnvironmentPolicy: 'Disabled'
    allowConfigFileUpdates: true
  }
}

output staticWebAppName string = demoSite.name
output staticWebAppUrl string = 'https://${demoSite.properties.defaultHostname}'
