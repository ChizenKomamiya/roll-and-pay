targetScope = 'resourceGroup'

param location string
param staticWebAppLocation string
param appName string
param tags object
param entraTenantId string
param apiClientId string
param spaClientId string
param administratorObjectId string
param maximumInstanceCount int
param monthlyBudgetAmount int
param budgetStartDate string
param budgetContactEmails array

var suffix = uniqueString(resourceGroup().id)
var databaseDefinition = loadJsonContent('../database/container-definition.json')
var cosmosDatabaseName = databaseDefinition.databaseName
var cosmosContainerName = databaseDefinition.containerName
var deploymentContainerName = 'function-releases'

resource staticSite 'Microsoft.Web/staticSites@2024-11-01' = {
  name: 'swa-${appName}-prod'
  location: staticWebAppLocation
  tags: tags
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {
    // GitHubのワークフロー作成・コード配置はこのテンプレートから行わない。
    provider: 'Custom'
    stagingEnvironmentPolicy: 'Disabled'
    allowConfigFileUpdates: true
  }
}

module demo './demo.bicep' = {
  name: 'rollpay-demo-site'
  params: {
    appName: appName
    staticWebAppLocation: staticWebAppLocation
    tags: union(tags, { purpose: 'demo' })
  }
}

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'law-${appName}-prod-jpe'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    workspaceCapping: {
      // 50 MB/日。反映には遅延があり、請求のハードリミットではない。
      dailyQuotaGb: json('0.05')
    }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${appName}-prod-jpe'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    IngestionMode: 'LogAnalytics'
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'st${appName}${suffix}'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'None'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {}
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: deploymentContainerName
  properties: {
    publicAccess: 'None'
  }
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: 'cosmos-${appName}-prod-${suffix}'
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    capabilities: [
      { name: 'EnableServerless' }
    ]
    enableFreeTier: false
    enableAutomaticFailover: false
    enableMultipleWriteLocations: false
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    backupPolicy: {
      type: 'Continuous'
      continuousModeProperties: {
        tier: 'Continuous7Days'
      }
    }
  }
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: cosmos
  name: cosmosDatabaseName
  properties: {
    resource: {
      id: cosmosDatabaseName
    }
    // Serverlessではthroughput/autoscaleSettingsを指定しない。
    options: {}
  }
}

resource appContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: cosmosContainerName
  properties: {
    resource: {
      id: cosmosContainerName
      partitionKey: databaseDefinition.partitionKey
      // TTL無効。DB単独の反映スクリプトと同じ定義を参照する。
      indexingPolicy: databaseDefinition.indexingPolicy
    }
    options: {}
  }
}

resource plan 'Microsoft.Web/serverfarms@2024-11-01' = {
  name: 'plan-${appName}-prod-jpe'
  location: location
  tags: tags
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2024-11-01' = {
  name: 'func-${appName}-prod-${suffix}'
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    clientAffinityEnabled: false
    functionAppConfig: {
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deploymentContainer.name}'
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: 2048
        alwaysReady: []
      }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      ftpsState: 'Disabled'
      cors: {
        allowedOrigins: ['https://${staticSite.properties.defaultHostname}']
        supportCredentials: false
      }
      appSettings: [
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
        // Auth/Cosmos設定の読取りと認証処理はAPI実装側で行う。
        { name: 'Auth__TenantId', value: entraTenantId }
        { name: 'Auth__ApiClientId', value: apiClientId }
        { name: 'Auth__AllowedSpaClientId', value: spaClientId }
        { name: 'Auth__AdministratorObjectId', value: administratorObjectId }
        { name: 'Auth__RequiredScope', value: 'access_as_user' }
        { name: 'Cosmos__Endpoint', value: cosmos.properties.documentEndpoint }
        { name: 'Cosmos__DatabaseName', value: cosmosDatabaseName }
        { name: 'Cosmos__ContainerName', value: cosmosContainerName }
      ]
    }
  }
}

resource scmPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-11-01' = {
  parent: functionApp
  name: 'scm'
  properties: { allow: false }
}

resource ftpPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-11-01' = {
  parent: functionApp
  name: 'ftp'
  properties: { allow: false }
}

// 作成後のprincipalIdをモジュールに渡して、ロール割り当て名もIDから生成する。
module dataAccess './data-access.bicep' = {
  name: 'rollpay-data-access'
  params: {
    functionPrincipalId: functionApp.identity.principalId
    storageAccountName: storage.name
    cosmosAccountName: cosmos.name
    databaseName: database.name
  }
}

resource budget 'Microsoft.Consumption/budgets@2024-08-01' = {
  name: 'budget-${appName}-prod'
  properties: {
    amount: monthlyBudgetAmount
    category: 'Cost'
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: budgetStartDate
    }
    notifications: {
      actual50: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 50
        thresholdType: 'Actual'
        contactEmails: budgetContactEmails
        locale: 'ja-jp'
      }
      actual80: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 80
        thresholdType: 'Actual'
        contactEmails: budgetContactEmails
        locale: 'ja-jp'
      }
      actual100: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        thresholdType: 'Actual'
        contactEmails: budgetContactEmails
        locale: 'ja-jp'
      }
      forecast100: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 100
        thresholdType: 'Forecasted'
        contactEmails: budgetContactEmails
        locale: 'ja-jp'
      }
    }
  }
}

output functionAppName string = functionApp.name
output apiBaseUrl string = 'https://${functionApp.properties.defaultHostName}/api/v1'
output staticWebAppName string = staticSite.name
output staticWebAppUrl string = 'https://${staticSite.properties.defaultHostname}'
output demoStaticWebAppName string = demo.outputs.staticWebAppName
output demoStaticWebAppUrl string = demo.outputs.staticWebAppUrl
output functionPrincipalId string = functionApp.identity.principalId
output cosmosAccountName string = cosmos.name
output cosmosEndpoint string = cosmos.properties.documentEndpoint
output databaseName string = cosmosDatabaseName
output containerName string = cosmosContainerName
