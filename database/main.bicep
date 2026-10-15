targetScope = 'resourceGroup'

@description('作成済みのCosmos DB for NoSQL Serverlessアカウント名。アカウント自体は作成・変更しない。')
param cosmosAccountName string

var definition = loadJsonContent('./container-definition.json')

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' existing = {
  name: cosmosAccountName
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: cosmos
  name: definition.databaseName
  properties: {
    resource: {
      id: definition.databaseName
    }
    // Serverlessなので固定RU・自動スケールを指定しない。
    options: {}
  }
}

resource appContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: definition.containerName
  properties: {
    resource: {
      id: definition.containerName
      partitionKey: definition.partitionKey
      indexingPolicy: definition.indexingPolicy
      // defaultTtl省略でTTL無効。Unique Keyも追加しない。
    }
    options: {}
  }
}

output databaseName string = database.name
output containerName string = appContainer.name
