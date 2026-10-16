# CI/CD（資産別）

GitHub Actionsの入口はルートの `.github/workflows/`。この `pipeline/` には実行スクリプトと運用手順をまとめる。ワークフローYAMLを `pipeline/` だけに置いてもGitHubは自動検出しない。

## トリガーと配置範囲

| ワークフロー | 対象変更 | mainへのpush | PR | 手動実行 |
|---|---|---|---|---|
| [Frontend](../.github/workflows/frontend.yml) | `frontend/**`、`pipeline/frontend/**`、自身のYAML | テスト・ビルド後、SWA本番へ自動配置 | テスト・ビルドのみ | mainから再配置 |
| [Backend](../.github/workflows/backend.yml) | `backend/**`、`pipeline/backend/**`、自身のYAML | テスト・発行後、既存Functionsへ自動配置 | テスト・発行確認 | mainから再配置 |
| [Infrastructure](../.github/workflows/infrastructure.yml) | `infra/**`、共通DB定義、`pipeline/infrastructure/**`、自身のYAML | オフラインテスト・Bicepコンパイルのみ | 同左 | `validate` / `what-if` / `apply` |
| [Database](../.github/workflows/database.yml) | `database/**`、`pipeline/database/**`、自身のYAML | オフラインテスト・Bicepコンパイルのみ | 同左 | `validate` / `what-if` / `apply` |
| [Pipeline definitions](../.github/workflows/pipelines.yml) | ワークフロー定義、`pipeline/tests/**` | トリガー分離テスト・PowerShell構文検証 | 同左 | 検証のみ |

資産側のREADME更新は対象外。`README.md`、`docs/`、`spec/`、この手順書だけの変更では配置しない。フロントエンドの変更でバックエンドを配置することも、その逆もない。両資産を同時に変更したコミットでは両方が独立して実行される。

`database/container-definition.json` は両方のBicepが参照するため、InfrastructureとDatabaseの検証が両方動く。自動でAzureへ反映はしない。

手動のクラウド操作はmainのみ。フロントとバックの自動実行は資産ごとに直列化し、別資産を待たせない。インフラ・DBの手動Azure操作は共通のconcurrency groupで直列化する。GitHub外の手動配置までは排他しない。

## フロントエンド：最初に設定するもの

対象：`rg-rollpay-prod-jpe` の `swa-rollpay-prod`。

1. Azure Portalで当該Static Web Appの「デプロイトークンの管理」を開く。
2. GitHubの [リポジトリのActions Secrets設定](https://github.com/ChizenKomamiya/roll-and-pay/settings/secrets/actions) → **New repository secret**。
3. 名前を **`AZURE_STATIC_WEB_APPS_API_TOKEN`**、値をこのSWAの配置トークンとして保存する。
4. GitHubの **Actions → Frontend → Run workflow → main** で初回配置する。その後は対象パスの変更をmainへpushすると自動配置される。

トークンはチャット・ソース・コミット・コマンドラインに貼らない。既存の同名Secretが正しいSWAを指していればそのまま使える。Secretはリソース作成用のAzure資格情報とは別。

Node.js 24とpnpm 11.25.0を使い、`install --frozen-lockfile` → 48件の現行テスト → 型チェック・本番ビルド → `frontend/dist` だけを配置する。Azure側では再ビルドせず、APIも同梱しない。プレビュー環境は作らずmainを本番へ配置する。

公開先：[Roll & Pay](https://jolly-river-0ce97e200.4.azurestaticapps.net/)。現時点はデモモードで、記録は閲覧者のブラウザー内に保存される。Microsoftログイン・業務API接続は未実装。

## バックエンド：OIDCの準備

Functionsの共有キー・公開プロファイルは使わず、Azure/loginのOIDCで既存Flex Consumptionのアプリへ配置する。GitHubのSecrets and variables → Actions → **Variables** に次を設定する。

配置専用IDがまだなければ、[infra/scriptsの作成手順](../infra/scripts/README.md#5-github-actionsのバックエンド配置専用id)に従って`Register-BackendDeploymentIdentity.ps1`を実行する。既存Function App用のマネージドID・main限定のOIDC連携・配置権限を作成し、以下の4変数をまとめて返す。返された値をGitHubへ登録してからBackendを再実行する。

| Repository Variable | 値 |
|---|---|
| `ROLLPAY_AZURE_TENANT_ID` | 配置先テナントID |
| `ROLLPAY_AZURE_SUBSCRIPTION_ID` | 配置先サブスクリプションID |
| `ROLLPAY_BACKEND_DEPLOY_CLIENT_ID` | バックエンド配置専用のEntraアプリ／User-assigned Managed IdentityのClient ID |
| `ROLLPAY_FUNCTION_APP_NAME` | `rollpay-prod`デプロイ出力の`functionAppName` |

配置用IDはSPA／業務APIのアプリ登録や、Functions実行用のSystem-assigned Managed Identityとは別に用意する。GitHubとのフェデレーションは次を使用する。

- Issuer：`https://token.actions.githubusercontent.com`
- Audience：`api://AzureADTokenExchange`
- Subject：`repo:ChizenKomamiya@33175412/roll-and-pay@1418381637:ref:refs/heads/main`

このリポジトリはGitHubの数値ID入りSubjectを使用する。旧形式の`repo:ChizenKomamiya/roll-and-pay:ref:refs/heads/main`では`AADSTS700213`で失敗する。旧版の準備スクリプトを実行済みなら、修正版を同じ引数で再実行すると、同じ配置IDのSubjectだけを更新できる。Client IDの再登録は不要。[GitHubの仕様変更](https://github.blog/changelog/2026-04-23-immutable-subject-claims-for-github-actions-oidc-tokens/)

Azure側で当該Function Appへのコード配置に必要な権限を付与する（公式例のWebsite Contributor等を対象スコープで確認）。リソース作成・DBデータへの権限をバックエンド配置IDへ一括付与しない。PRではAzure/login・配置のステップを実行しない。

`.NET 10`で`backend/tests/RollAndPay.Api.Tests.csproj`のテスト後に`dotnet publish`し、`backend/bin/ci-publish`を`Azure/functions-action`で既存アプリに発行する。`local.settings*.json`・テスト資材・初期登録ツールの発行除外を確認してから配置する。G01は認証とA01、G02はDB接続・セッションとA02、G03はゲーム状態取得A04・A05を実装。PRはテスト・発行物の確認だけを行い、Azureへ配置しない。エミュレーター統合4件（G02・G03各2件）は通常のCIでスキップし、別途ローカル実行する。

Database CIは.NET 10で初期登録ツールの入力検証・バッチ構築のテストも実行する。登録ツールはAPIの保存モデルを参照するため、APIプロジェクトを直接restoreしてからテストする。CIから参加者データを投入せず、専用ツールの実行は[databaseの手順](../database/README.md)に従う。

## インフラ・DB：検証と手動反映

push／PRの検証ではAzureログインせず、Azure上の設定を読み書きしない。パラメーターの構文確認には架空のIDと連絡先を使う。Bicep CLIの取得にはネットワーク接続が必要。

実反映するときは、Actionsで対象ワークフローの **Run workflow → main → operation** を選ぶ。

- `validate`：既定。ローカル検証のみ。
- `what-if`：Azureでの差分確認のみ。
- `apply`：検証後、Azureへ反映。

先に`what-if`を実行して差分を確認し、意図した変更なら改めて`apply`を選ぶ。両実行の間の変更を固定・ロックするplan方式ではない。反映は選んだmainコミットを使う。GitHub Environmentsの`infrastructure-production`、`database-production`にはmain限定の配置ブランチ制限を設定でき、契約プランに応じて承認者も設定できる。

クラウド操作用の共通Variablesは上記のTenant ID・Subscription ID。追加設定は以下。

| 用途 | Variables | Secrets |
|---|---|---|
| Infrastructure | `ROLLPAY_INFRA_DEPLOY_CLIENT_ID`、`ROLLPAY_API_CLIENT_ID`、`ROLLPAY_SPA_CLIENT_ID`、`ROLLPAY_BUDGET_START_DATE` | `ROLLPAY_ADMIN_OBJECT_ID`、`ROLLPAY_BUDGET_EMAIL` |
| Database | `ROLLPAY_DATABASE_DEPLOY_CLIENT_ID`、`ROLLPAY_COSMOS_ACCOUNT_NAME` | なし |

`ROLLPAY_API_CLIENT_ID`と`ROLLPAY_SPA_CLIENT_ID`は登録済みの業務API／SPAのClient ID。`ROLLPAY_BUDGET_START_DATE`は既存予算と同じ値（初回配置月の`2026-10-01T00:00:00Z`）を維持する。

手動配置用IDのOIDC Issuer/Audienceは上記と同じ。Subjectは環境ごとに分ける。

- インフラ：`repo:ChizenKomamiya@33175412/roll-and-pay@1418381637:environment:infrastructure-production`
- DB：`repo:ChizenKomamiya@33175412/roll-and-pay@1418381637:environment:database-production`

インフラ用IDには既存[インフラ手順](../infra/README.md)に記載のサブスクリプションデプロイ・RG／リソース管理・ロール割り当て権限が必要。DB用IDには[DB手順](../database/README.md)の管理プレーン権限が必要。自動ワークフローからID作成・権限付与は行わない。OIDC設定・RBACが済んでいなければクラウド操作は失敗する。

インフラのwhat-ifは設定値・通知先をログへ出さないようResourceIdOnlyを使う。詳細な設定差分の確認が必要な場合は、既存のローカル手順で確認する。DBは既存の互換性検証付きApply-Schema.ps1を呼ぶ。APIコード・固定参加者の初期データをIaCと同時に配置しない。

## ローカルから同じ検証を実行

リポジトリのルートでPowerShell 7から実行する。

```powershell
./pipeline/frontend/Build.ps1
./pipeline/backend/Build.ps1
./pipeline/infrastructure/Validate.ps1
./pipeline/database/Validate.ps1
node --test pipeline/tests/trigger-filters.test.mjs
```

必要なNode.js／pnpm・.NET SDK・Azure CLIは各資産のREADMEを参照。LinuxのGitHub-hosted runnerでも同じPowerShellスクリプトを使う。

設定未登録の場合、mainのフロント／バックの実行はビルド成功後の配置ステップで失敗する。設定後に対象ワークフローをmainから手動実行するか、失敗したrunを再実行する。

## 確認範囲

ローカルで共通スクリプト、変更パスの分離、ワークフローの静的構文を検証する。GitHub上のビルド・検証は初回実行済み。Secret／OIDC／RBACを設定した後の実デプロイの成功は別途確認が必要。

2026年10月16日：Windows／PowerShell 7でフロントエンド48テスト・ビルド、バックエンド発行とローカル設定除外、インフラ／DBのオフラインテストとBicepコンパイルが成功。変更パスの分離38テスト、PowerShell構文、actionlint 1.7.12による5ワークフローの静的検証が成功。Azureリソースへの反映は実行していない。

同日のGitHub初回実行（`f181f39`、Ubuntu runner）：

- [Frontend](https://github.com/ChizenKomamiya/roll-and-pay/actions/runs/38811378061)：テスト・ビルド成功。`AZURE_STATIC_WEB_APPS_API_TOKEN`未登録のため、配置前の確認ステップで停止。
- [Backend](https://github.com/ChizenKomamiya/roll-and-pay/actions/runs/38811378020)：発行確認成功。配置用Client IDのVariable未登録のため、Azureログイン前に停止。
- [Infrastructure](https://github.com/ChizenKomamiya/roll-and-pay/actions/runs/38811377998)、[Database](https://github.com/ChizenKomamiya/roll-and-pay/actions/runs/38811377963)：検証成功。自動pushでは反映ジョブをスキップ。
- [Pipeline definitions](https://github.com/ChizenKomamiya/roll-and-pay/actions/runs/38811377947)：トリガー分離テスト・PowerShell構文の検証成功。

同日、その後の[Backend #10（3回目）](https://github.com/ChizenKomamiya/roll-and-pay/actions/runs/38855735004/attempts/3)でビルド・設定チェック・Azure OIDCログイン・Functions配置がすべて成功。2回目の`AADSTS700213`はGitHubの数値ID入りSubjectとAzure側の旧Subjectの不一致が原因で、配置専用IDのSubjectを更新して解消した。初期設定スクリプトと上記Subject例も修正済み。業務APIの実データ疎通は別途確認する。

参考：[GitHubの配置ディレクトリ](https://docs.github.com/en/actions/tutorials/create-an-example-workflow)、[変更パスのフィルター](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow)、[SWAビルド構成](https://learn.microsoft.com/azure/static-web-apps/build-configuration)、[FunctionsのGitHub Actions配置](https://learn.microsoft.com/azure/azure-functions/functions-how-to-github-actions)。
