targetScope = 'subscription'

@description('本番リソースグループ名。再配置時も同じ値を使う。')
param resourceGroupName string = 'rg-rollpay-prod-jpe'

@description('API・Storage・Cosmos DB・監視のリージョン。Flexの対応状況を構築前に確認する。')
param location string = 'japaneast'

@description('SWA対応リージョン。静的コンテンツはグローバル配信される。')
param staticWebAppLocation string = 'eastasia'

@description('リソース名の接頭辞。小文字英数字のみを指定する。')
@minLength(3)
@maxLength(9)
param appName string = 'rollpay'

@description('既存Entra IDテナント。通常は配置先サブスクリプションのテナント。')
@minLength(36)
@maxLength(36)
param entraTenantId string = tenant().tenantId

@description('事前に登録したAPIアプリのClient ID。シークレットではない。')
@minLength(36)
@maxLength(36)
param apiClientId string

@description('事前に登録したSPAアプリのClient ID。シークレットではない。')
@minLength(36)
@maxLength(36)
param spaClientId string

@description('既存テナント内の管理者ユーザーObject ID。アプリのObject IDではない。')
@minLength(36)
@maxLength(36)
param administratorObjectId string

@description('月額予算。契約の請求通貨単位。JPY契約で1000は1,000円。')
@minValue(1)
param monthlyBudgetAmount int = 1000

@description('予算開始月の1日、例 2026-10-01T00:00:00Z。再配置時にも維持する。')
@minLength(20)
param budgetStartDate string

@description('予算通知先。実メールアドレスはローカルパラメータまたは環境変数で指定する。')
@minLength(1)
@maxLength(5)
param budgetContactEmails array

@description('Flex最大インスタンス数。現行の設定下限は40。費用上限にはならない。')
@minValue(40)
@maxValue(1000)
param maximumInstanceCount int = 40

var tags = {
  application: appName
  environment: 'prod'
  managedBy: 'bicep'
}

resource production 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

module resources './resources.bicep' = {
  name: 'rollpay-production-resources'
  scope: production
  params: {
    location: location
    staticWebAppLocation: staticWebAppLocation
    appName: appName
    tags: tags
    entraTenantId: entraTenantId
    apiClientId: apiClientId
    spaClientId: spaClientId
    administratorObjectId: administratorObjectId
    maximumInstanceCount: maximumInstanceCount
    monthlyBudgetAmount: monthlyBudgetAmount
    budgetStartDate: budgetStartDate
    budgetContactEmails: budgetContactEmails
  }
}

output resourceGroupId string = production.id
output functionAppName string = resources.outputs.functionAppName
output apiBaseUrl string = resources.outputs.apiBaseUrl
output staticWebAppName string = resources.outputs.staticWebAppName
output staticWebAppUrl string = resources.outputs.staticWebAppUrl
output demoStaticWebAppName string = resources.outputs.demoStaticWebAppName
output demoStaticWebAppUrl string = resources.outputs.demoStaticWebAppUrl
output functionPrincipalId string = resources.outputs.functionPrincipalId
output cosmosAccountName string = resources.outputs.cosmosAccountName
output cosmosEndpoint string = resources.outputs.cosmosEndpoint
output databaseName string = resources.outputs.databaseName
output containerName string = resources.outputs.containerName
