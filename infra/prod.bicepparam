using './main.bicep'

// 実値は環境変数から読む。ID・実メールアドレスをこのファイルに書き込まない。
param resourceGroupName = 'rg-rollpay-prod-jpe'
param location = 'japaneast'
param staticWebAppLocation = 'eastasia'
param apiClientId = readEnvironmentVariable('ROLLPAY_API_CLIENT_ID')
param spaClientId = readEnvironmentVariable('ROLLPAY_SPA_CLIENT_ID')
param administratorObjectId = readEnvironmentVariable('ROLLPAY_ADMIN_OBJECT_ID')
param budgetStartDate = readEnvironmentVariable('ROLLPAY_BUDGET_START_DATE')
param budgetContactEmails = [readEnvironmentVariable('ROLLPAY_BUDGET_EMAIL')]
param monthlyBudgetAmount = 1000
