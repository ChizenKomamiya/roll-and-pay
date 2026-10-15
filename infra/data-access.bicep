targetScope = 'resourceGroup'

param functionPrincipalId string
param storageAccountName string
param cosmosAccountName string
param databaseName string

// HTTP-only host用。Blobトリガー等を追加する場合は必要な権限を再確認する。
var blobDataOwnerRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' existing = {
  name: cosmosAccountName
}

// 同一アカウントでホストと配布を共用。Ownerは配布に必要なContributorを包含する。
resource hostStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, functionPrincipalId, blobDataOwnerRoleId)
  properties: {
    roleDefinitionId: blobDataOwnerRoleId
    principalId: functionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource cosmosDataRole 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmos
  name: guid(cosmos.id, functionPrincipalId, databaseName, 'data-contributor')
  properties: {
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: functionPrincipalId
    scope: '${cosmos.id}/dbs/${databaseName}'
  }
}
