# Azure IDの準備スクリプト

PowerShell 7＋Azure CLIを使用する。Microsoft Graph PowerShellモジュールのインストールは不要。利用者ログイン用のAPI／SPA登録と、GitHub Actionsの配置専用IDをここで準備する。

2026年10月15日、ローカルのAzure CLI 2.61.0で使用コマンドのヘルプ・引数形式を確認。`az ad signed-in-user show`は`--subscription`を受け付けないため、共通処理からの一律追加を廃止した。Entra準備スクリプトの実テナント動作は未検証。後続のFlex配置・コード配置では最新安定版のAzure CLIとBicep CLIを推奨する。

| スクリプト | 内容 |
|---|---|
| `Get-AdministratorObjectId.ps1` | 対象テナントにログイン中の本人Object IDを取得。Azureへの書込みなし |
| `Register-EntraApplications.ps1` | Single tenantのAPI／SPA登録、スコープ、SPA事前承認、Service Principalを作成・設定 |
| `Register-BackendDeploymentIdentity.ps1` | 既存Function Appへの配置専用マネージドID、GitHub mainとのOIDC連携、アプリ単体の配置権限を作成・設定 |
| `AzureCli.Common.ps1` | CLI・Graph・ローカル出力の共通処理。直接実行不要 |

## 1. 対象テナントにログイン

リポジトリのルートで、管理者本人の個人Microsoftアカウントを使って既存テナントにログインする。

```powershell
$tenantId = '<既存テナントID>'
$subscriptionId = '<AzureサブスクリプションID>'
az login --tenant $tenantId
az account set --subscription $subscriptionId
```

`-SubscriptionId`は現在選択済みのサブスクリプションとの一致確認に使う。スクリプト内で既定値を変更せず、Entraコマンドにもこの引数を渡さない。不一致の場合は上記の`az account set`を実行してから再試行する。

AzureリソースのOwner権限とEntraのアプリ管理権限は別の権限。アプリ登録の作成・更新、Service Principalの作成、Graphでの読取りができるEntra権限も必要。権限不足の場合はCLIエラーで停止し、自動で管理権限を追加しない。

## 2. 本人のObject IDだけ取得

```powershell
$identity = & ./infra/scripts/Get-AdministratorObjectId.ps1 `
    -TenantId $tenantId -SubscriptionId $subscriptionId `
    -SetEnvironmentVariables

$identity
```

`ROLLPAY_ADMIN_OBJECT_ID`が現在のPowerShellプロセスに設定される。取得するのは対象テナント内のユーザーObject IDで、ホームテナントのIDやアプリObject IDではない。サービスプリンシパルでのログインは対象外。

必要なら`-OutputPath ./infra/administrator.local.json`でID・テナント・サブスクリプションを保存できる。氏名・メールアドレス・トークンは出力しない。

## 3. アプリ登録の準備

まず予定を表示する。`-WhatIf`でもアカウント・ユーザー・既存アプリの読取りは行うが、作成・更新・ローカル設定保存は行わない。

```powershell
& ./infra/scripts/Register-EntraApplications.ps1 `
    -TenantId $tenantId -SubscriptionId $subscriptionId -WhatIf
```

実際に登録する場合：

```powershell
$apps = & ./infra/scripts/Register-EntraApplications.ps1 `
    -TenantId $tenantId -SubscriptionId $subscriptionId `
    -SetEnvironmentVariables `
    -OutputPath ./infra/entra.local.json

$apps
```

作成・設定内容：

- `rollpay-api-prod`と`rollpay-spa-prod`を`AzureADMyOrg`（Single tenant）で登録。
- APIに`api://<API Client ID>`、v2アクセストークン、Admin型の委任スコープ`access_as_user`を設定。
- SPAの要求API権限に当該スコープを追加。
- APIの`preAuthorizedApplications`に当該SPAとスコープを登録。このスコープについてユーザー同意を不要にする事前承認方式。別途テナント全体の`oauth2PermissionGrant`やGraphのアプリケーション権限を追加する方式は採用しない。[Microsoft Graphの事前承認](https://learn.microsoft.com/en-us/graph/api/resources/preauthorizedapplication?view=graph-rest-1.0)
- SPAのリダイレクトURIは既定で`http://localhost:5173/`。クライアントシークレットは作らず、PKCEを使う前提。
- APIとSPAのService Principal（Enterprise application）も作成。

この事前承認は「管理者本人のみ許可」の代わりにはならない。本人の`tid`・`oid`とスコープを検証するAPI実装は引き続き必要。

`-SetEnvironmentVariables`で次の3変数を設定する。`$apps`にもClient ID、Object ID、スコープ等を返す。

```powershell
# スクリプトが設定するもの
$env:ROLLPAY_API_CLIENT_ID
$env:ROLLPAY_SPA_CLIENT_ID
$env:ROLLPAY_ADMIN_OBJECT_ID

# デプロイ前に別途設定するもの
$env:ROLLPAY_BUDGET_START_DATE = '2026-10-01T00:00:00Z'
$env:ROLLPAY_BUDGET_EMAIL = '<予算通知先>'
```

環境変数は同じPowerShellプロセスだけに設定される。`pwsh -File ...`で別プロセスとして実行した場合、呼出元には戻らない。上記のように`&`で実行するか、保存した`infra/entra.local.json`から読み込む。

```powershell
$apps = Get-Content ./infra/entra.local.json -Raw | ConvertFrom-Json
$env:ROLLPAY_API_CLIENT_ID = $apps.ApiClientId
$env:ROLLPAY_SPA_CLIENT_ID = $apps.SpaClientId
$env:ROLLPAY_ADMIN_OBJECT_ID = $apps.AdministratorObjectId
```

実ID入りのJSONは`infra/*.local.json`として保存し、Gitに含めない。デプロイの続きは[インフラ手順](../README.md)のvalidate／what-ifへ進む。

## 4. SWA配置後のURL追加

本番URLが決まったら同じスクリプトを再実行し、アプリが実際に使うリダイレクトURIを指定する。MSALのURIとパス・末尾スラッシュも一致させる。

```powershell
& ./infra/scripts/Register-EntraApplications.ps1 `
    -TenantId $tenantId -SubscriptionId $subscriptionId `
    -SpaRedirectUris @('http://localhost:5173/', 'https://<SWA既定ホスト名>/') `
    -SetEnvironmentVariables
```

URIは既存リストに追加し、既存URIを削除しない。HTTPSまたはlocalhostのHTTPだけを受け付ける。リダイレクトURIの登録と、FunctionsのCORS許可Originは別設定。

## 5. GitHub Actionsのバックエンド配置専用ID

配置専用IDは、GitHub ActionsがAzureにコードを配置する際の実行者。ここではAzureの「ユーザー割り当てマネージドID」を作る。Azureが発行する`clientId`（Client ID）が、GitHubへ登録する識別番号になる。パスワードやクライアントシークレットは作成しない。API／SPAのアプリ登録、FunctionsがDBへ接続する実行用IDとは別にする。

このスクリプトは、すでにあるFunction Appに対して実行する。リポジトリのルートで、セクション1の`az login`とサブスクリプション選択を済ませる。実行者には、対象RG内のマネージドID・フェデレーション設定の作成権限と、対象Function Appでのロール割り当て権限が必要。例えば対象RGのOwnerなら両方を満たす。Contributorだけではロールを割り当てられない。スクリプトから実行者の権限を昇格しない。Entraアプリ登録の作成権限は不要。

既存の配置出力からFunction App名を取得し、まず変更予定を確認する。

```powershell
$functionAppName = az deployment sub show --name rollpay-prod `
    --subscription $subscriptionId `
    --query properties.outputs.functionAppName.value --output tsv
if ($LASTEXITCODE -ne 0 -or -not $functionAppName) { throw 'Function App名を取得できません。' }

$deploymentOptions = @{
    TenantId = $tenantId
    SubscriptionId = $subscriptionId
    FunctionAppName = $functionAppName
}
& ./infra/scripts/Register-BackendDeploymentIdentity.ps1 @deploymentOptions -WhatIf
```

表示内容を確認したら、同じPowerShellで作成する。

```powershell
$deployment = & ./infra/scripts/Register-BackendDeploymentIdentity.ps1 @deploymentOptions `
    -OutputPath ./infra/backend-deployment.local.json

$deployment.RepositoryVariables
```

作成・設定するもの：

- `rg-rollpay-prod-jpe`に`id-rollpay-backend-deploy-prod`というマネージドIDを作成。既存のFunction Appと同じリージョンを使う。
- GitHubのOIDC Issuer `https://token.actions.githubusercontent.com`、Audience `api://AzureADTokenExchange`を登録。
- Subjectを`repo:ChizenKomamiya@33175412/roll-and-pay@1418381637:ref:refs/heads/main`に限定する。現在のBackendワークフローに合わせ、PRや別ブランチからのAzureログインは許可しない。数値はGitHubのowner IDとrepository IDで、AzureのClient IDではない。
- このIDに`Website Contributor`を**指定Function App単体**のスコープで付与する。RG／サブスクリプション全体のContributor、DBデータ権限、Storageデータ権限は付与しない。Website Contributorは対象アプリのコード・設定等を管理できる権限。

続いて、出力された`RepositoryVariables`の**4項目すべて**を[GitHubのActions Variables](https://github.com/ChizenKomamiya/roll-and-pay/settings/variables/actions)の「New repository variable」で登録する。現在のワークフローはVariablesを参照する。

| 名前 | スクリプトが返す値 |
|---|---|
| `ROLLPAY_BACKEND_DEPLOY_CLIENT_ID` | 新しいマネージドIDのClient ID。Principal ID／Object IDではない |
| `ROLLPAY_AZURE_TENANT_ID` | 指定したTenant ID |
| `ROLLPAY_AZURE_SUBSCRIPTION_ID` | 指定したSubscription ID |
| `ROLLPAY_FUNCTION_APP_NAME` | 確認した既存Function App名 |

このスクリプトはAzure側の準備とローカル結果の保存までを行う。GitHubへの登録とコード配置は実行しない。4変数の登録後、**Actions → Backend → Run workflow → main**で配置する。権限・OIDCの反映には数分かかる場合があるため、作成直後のログイン／配置失敗は設定とログを確認して再実行する。

同じコマンドの再実行では、管理タグと配置先が一致するID・OIDC設定・権限を再利用する。旧版が作った同一repo/mainの名前だけのSubjectは、数値ID入りの形式へ更新する。Client ID・配置権限は維持し、旧Subjectを残さない。途中で権限不足などが起きた場合、作成済みIDを保持し、成功結果ファイルは出力しない。原因解消後、同じ引数で再開する。並行実行はしない。同名の管理外ID、配置先の変更、その他のOIDC設定の不一致は自動で上書きせず停止する。名前を変更する場合だけ`-IdentityName`を指定し、以後も同じ名前を使う。

### AADSTS700213でAzureログインに失敗する場合

2026年7月15日以降に作成されたGitHubリポジトリは、名前に加えて数値IDを含むSubjectを使用する。[GitHubの仕様変更](https://github.blog/changelog/2026-04-23-immutable-subject-claims-for-github-actions-oidc-tokens/)。旧版スクリプトのSubjectと、実際のエラーログに出るSubjectが一致しない場合は、修正版を同じ引数で再実行する。`-WhatIf`でも移行予定を確認できる。GitHub Variablesやパスワードの変更、Actions内での対話的な`az login`は不要。

`-WhatIf`は既存設定の読取りと予定表示のみで、Azureや結果ファイルを書き換えない。IDをFunctionsの実行用Identityに関連付けたり、既存の権限を削除したりはしない。既存IDに手作業で追加した権限まで検査・除去する仕組みではない。

参考：[FunctionsのGitHub ActionsとOIDC](https://learn.microsoft.com/en-us/azure/azure-functions/functions-how-to-github-actions)、[マネージドIDのフェデレーション設定](https://learn.microsoft.com/en-us/cli/azure/identity/federated-credential?view=azure-cli-latest)。

## Entraアプリ登録の再実行・失敗時

スクリプトが作成したアプリに管理タグ`rollpay-managed:entra-bootstrap:v1`を付ける。同名・同タグのアプリはClient IDと既存スコープIDを維持して再利用する。同名の管理外アプリ、複数の同名アプリ、種類不一致・無効なスコープは停止する。GUIで作った同名アプリには勝手にタグを付けて取り込まない。名前を変える場合は`-ApiDisplayName`／`-SpaDisplayName`を両方明示し、以後も同じ名前を使う。

Graphの一連の更新はトランザクションではない。途中失敗時に作成済みアプリを自動削除しない。権限や設定を確認し、同じパラメーターで再実行する。登録直後はGraphへの反映遅延で失敗することもある。並行実行はせず、複数アプリになった場合は手動確認する。

スコープの作成とSPA事前承認は別々のPATCHで行う。スコープIDがアプリの読取り結果に現れることを最大6回、2秒間隔で確認してから事前承認する。読取り後も`api.preAuthorizedApplications.delegatedPermissionIds`の参照先スコープが見つからない場合、そのエラーに限って事前承認を最大6回、2秒間隔で再試行する。権限不足など他のエラーは再試行しない。反映待ちの上限を超えた場合も既存アプリ・スコープIDは保持し、同じコマンドで再開できる。

## オフライン検証

```powershell
pwsh -NoProfile -File ./infra/tests/Test-EntraScripts.ps1
pwsh -NoProfile -File ./infra/tests/Test-BackendDeploymentIdentity.ps1
```

Azure CLIをモックに置き換えるので、実Azureへのアクセス・書込みは行わない。2026年10月15日、構文、初回登録、再実行のID維持、スコープ／権限の重複防止、URL追加、WhatIf、本人ID取得、テナント不一致・不正URI・既存アプリ衝突の拒否を確認。実テナントの権限・Graph反映・実ログインは実行時に確認する。

同日、スコープ新設と事前承認の同時更新によるPermission ID参照エラーをモックで再現し、段階的更新・読取り反映待ち・事前承認の限定再試行で通過することを確認した。

2026年10月16日：配置専用IDスクリプトのオフラインテストで、main限定の信頼、Function App単体の権限、再実行、途中失敗からの再開、WhatIf、出力4変数、テナント／サブスクリプション／配置先の不一致・管理外ID・OIDC設定衝突の拒否を確認。Infrastructure CIからも実行する。初版作成時点では実AzureでのID作成・権限付与と、GitHubからの配置は未実行。

同日、実Azure環境に対する`-WhatIf`も成功。選択中のテナント・サブスクリプション、既存Function App、配置専用IDの未作成を読み取りで確認し、作成・OIDC設定・Function App単体への権限付与の予定を表示した。Azureへの書込みは実行していない。

同日、その後ユーザーが作成したIDでBackend #10の再実行が`AADSTS700213`で停止。実際のSubjectにはGitHubのowner ID／repository IDが入り、初版が登録した名前だけのSubjectと不一致だった。修正版で同じIDのSubjectを更新し、Issuer／Audienceも再読取りで確認。ローカルAzure CLI 2.61ではupdateにもIssuerとSubjectの両方が必要なため明示した。旧形式からの移行、移行時のWhatIf、Client ID維持、別owner／repository IDの拒否をオフラインテストへ追加し、成功を確認。[Backend #10の3回目](https://github.com/ChizenKomamiya/roll-and-pay/actions/runs/38855735004/attempts/3)でAzure OIDCログインとFunctions配置がともに成功。アプリの業務API疎通はこの確認に含まない。

参考：[アプリ登録の作成](https://learn.microsoft.com/en-us/graph/api/application-post-applications?view=graph-rest-1.0)、[アプリ設定更新](https://learn.microsoft.com/en-us/graph/api/application-update?view=graph-rest-1.0)、[ログインユーザーの取得](https://learn.microsoft.com/en-us/cli/azure/ad/signed-in-user?view=azure-cli-latest)
