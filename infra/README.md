# AzureインフラのBicepテンプレート

[基本設計](../spec/infrastructure.md)と[DB設計](../spec/database-design.md)をもとにした初版。2026年10月15日、ユーザーが初回デプロイを実行し、親・子デプロイの成功と主要設定を確認した。[確認記録](deployment-verification.md)を参照。

## ファイル

| ファイル | 内容 |
|---|---|
| `main.bicep` | サブスクリプションスコープの入口。リソースグループを作成 |
| `resources.bicep` | SWA、Functions、Storage、DB、監視、予算 |
| `data-access.bicep` | Functions作成後のManaged Identityにデータアクセス権限を付与 |
| `../database/container-definition.json` | DB・コンテナー名、パーティション、インデックスの共通定義 |
| `prod.bicepparam` | 本番設定。実ID・通知先を環境変数から取得 |
| `scripts/` | Entraアプリ登録・本人Object ID取得のPowerShellスクリプト。[実行手順](scripts/README.md) |

## 作成する構成

- 本番1環境、基本リージョンJapan East。SWAのリソース配置先のみEast Asia、画面資材はグローバル配信。
- SWA Free。Functionsとのバックエンド関連付けは行わず、SWAの既定ホスト名をFunctionsのCORS許可Originに設定。プレビュー環境は無効。
- Functions Flex Consumption、Linux、.NET 10 Isolated、2,048 MB、Always readyなし、最大40インスタンス。
- Standard LRS Storage。匿名Blob公開・共有キー認証・FTP・SCM基本認証を無効化。
- Cosmos DB for NoSQL Serverless、単一リージョン、Session整合性、継続的バックアップ7日、キー認証なし。
- Database `rollpay` / Container `app` / Partition Key `/ownerKey`。TTLなし。DB設計の限定インデックスと2種類の複合インデックス。
- Application InsightsとLog Analytics（保持30日、日次取込上限50 MB）。取込上限は遅延・例外があり、請求のハードリミットではない。
- 当該リソースグループの月次費用予算。実績50%・80%・100%、予測100%超過でメール通知。予算は課金を停止しない。CIの料金等はこの予算に含まれない。

リソース名のサフィックスはリソースグループIDから生成する。再配置時はサブスクリプション、グループ名、appNameを維持する。appNameは小文字英数字3〜9文字を指定する。Storage名は24文字以内に収めている。

既存Cosmos DBアカウントにDB・コンテナー定義だけを反映する場合は、[databaseのスクリプト](../database/README.md)を使用できる。両方のBicepが同じJSON定義を参照する。インフラ配置とDB単独反映は同時に実行しない。

## パラメーター

| パラメーター | 指定方法・初期値 |
|---|---|
| `resourceGroupName` | `rg-rollpay-prod-jpe` |
| `location` | `japaneast` |
| `staticWebAppLocation` | `eastasia` |
| `appName` | `rollpay` |
| `entraTenantId` | 既定は配置先サブスクリプションのテナントID |
| `apiClientId` | `ROLLPAY_API_CLIENT_ID`：API登録のApplication (client) ID |
| `spaClientId` | `ROLLPAY_SPA_CLIENT_ID`：SPA登録のApplication (client) ID |
| `administratorObjectId` | `ROLLPAY_ADMIN_OBJECT_ID`：管理者ユーザーの対象テナント内Object ID |
| `monthlyBudgetAmount` | 1000。請求通貨がJPYの場合に1,000円。別通貨の場合は換算して変更 |
| `budgetStartDate` | `ROLLPAY_BUDGET_START_DATE`：初回配置月の1日UTC、例 `2026-10-01T00:00:00Z`。再配置時にも同じ値を使用 |
| `budgetContactEmails` | `ROLLPAY_BUDGET_EMAIL`：実通知先1件。複数宛先はローカルパラメーターファイルで指定 |
| `maximumInstanceCount` | 40。小人数利用でも費用上限を意味しない |

GUID・メールアドレス・日付の意味的な妥当性は構築前に確認する。実値入りファイルを作る場合は`*.local.bicepparam`にし、コンパイル結果は`infra/build/`に保存する。いずれもGit対象外。

## ローカル検証

Azure CLIとBicep CLIが必要。リポジトリのルートから実行する。環境変数の未設定時はパラメーターファイルのコンパイルを失敗させ、ダミーIDでの配置を防ぐ。

```powershell
az bicep install

$env:ROLLPAY_API_CLIENT_ID = '<API登録のClient ID>'
$env:ROLLPAY_SPA_CLIENT_ID = '<SPA登録のClient ID>'
$env:ROLLPAY_ADMIN_OBJECT_ID = '<対象テナント内の本人Object ID>'
$env:ROLLPAY_BUDGET_START_DATE = '2026-10-01T00:00:00Z'
$env:ROLLPAY_BUDGET_EMAIL = '<予算通知先メールアドレス>'

New-Item -ItemType Directory -Path infra/build -Force | Out-Null
az bicep build --file infra/main.bicep --outfile infra/build/main.json
az bicep build-params --file infra/prod.bicepparam --outfile infra/build/prod.parameters.json
```

Bicepコンパイルは型・構文・依存関係の検証。リージョンの提供状況、実ID、クォータ、予算の契約対応、リソース名の利用可否、実際のManaged Identity接続まで保証しない。

## Azureでの事前確認・将来の配置

以下は構築時に実行する手順で、今回のテンプレート作成では実行していない。

1. [Entra準備スクリプト](scripts/README.md)でSingle tenantのSPA／API登録を用意する。APIに`access_as_user`を公開し、v2アクセストークンを設定。SPAに委任権限を設定し、APIで当該SPAを事前承認する。
2. 管理者ユーザーのObject IDとログイン可能な状態、サブスクリプション・テナント・請求通貨を確認する。
3. FlexのJapan East、.NET 10、地域クォータを確認する。予算APIの契約対応・通知先も確認する。
4. 配置実行者にサブスクリプションでのRG作成権限、RG内リソース作成権限、StorageのRBAC割り当て権限、Cosmosデータロール割り当て権限を用意する。
5. 上記のローカルコンパイル後にログインして対象サブスクリプションを選び、validateとwhat-ifを実行する。

```powershell
az login
az account set --subscription '<対象サブスクリプションID>'

az deployment sub validate --name rollpay-prod --location japaneast --template-file infra/main.bicep --parameters infra/prod.bicepparam
az deployment sub what-if --name rollpay-prod --location japaneast --template-file infra/main.bicep --parameters infra/prod.bicepparam
```

新規作成時のSWAホスト名やManaged Identity IDは実行時に決まるため、what-ifが一部の変更を解決できない場合がある。既存リソースへの差分・置換の有無を確認する。

配置を行う段階でのみ、次を実行する。

```powershell
az deployment sub create --name rollpay-prod --location japaneast --template-file infra/main.bicep --parameters infra/prod.bicepparam
```

配置後は出力のSWA URLをSPAリダイレクトURIへ登録する。ローカルURIは別途登録し、本番APIのCORSにlocalhostを追加しない。

## API・画面の配置との接点

Bicepはリソースと設定の作成までを担当する。Entra登録、GitHub OIDC、CIワークフロー、APIコード、画面ビルド成果物、参加者の初期データは別途準備する。APIのBearer認証はBicepだけでは実装されない。全APIで本人・スコープ検証を実装してから本番コードを配置する。

Functions設定名はAPI側で次の構成セクションとして読む契約案とする。

| セクション | キー |
|---|---|
| `Auth` | `TenantId`, `ApiClientId`, `AllowedSpaClientId`, `AdministratorObjectId`, `RequiredScope` |
| `Cosmos` | `Endpoint`, `DatabaseName`, `ContainerName` |

Managed Identityには、共用StorageアカウントでStorage Blob Data Ownerを付与する。HTTP-onlyホストの必要権限であり、配布コンテナー書込みに必要なContributor権限も包含する。Cosmos DB Built-in Data Contributorは`rollpay`データベースのみに付与する。権限反映まで初回起動が失敗し得るため、反映を確認してからコード配置・疎通を行う。Identityを削除・再作成した場合は古いロール割り当ての整理も確認する。

FunctionsコードはFlex対応のOneDeploy経路で配置する。BlobにZIPを直接アップロードするだけではコード配置完了にはならない。SCM基本認証を無効にしているため、配置はEntra認証に対応したCLI／CIを使用する。

CORSのOriginはAzure側で設定する。API設計の許可メソッド・要求ヘッダーのプリフライト動作を確認し、`ETag, Location, Idempotency-Replayed, Retry-After, X-Read-Session`を`Access-Control-Expose-Headers`で公開する処理はAPI側で実装・検証する。重複するOrigin応答ヘッダーを付けない。

Application Insightsにはホスト／Worker両方のログ設定が関係する。コード配置時にサンプリング・ログレベル・個人情報除外を設定し、50 MB/日の上限だけを費用対策としない。RU・429・容量はCosmosのプラットフォームメトリクスで確認する。本文を含むDB詳細ログや有料のアクショングループ／追加アラートはこの初版では作成しない。

参加者初期データはDB設計のroster/control/lossTotalsを配置用IDで登録する。APIの実行IDやキーを使わず、必要データ権限を配置用IDに別途付与する。DBバックアップの復元・ログへの個人情報混入・認証拒否ケースは、構築時の受入確認に含める。

再配置すると、テンプレートに定義したFunctionsのアプリ設定が正となる。ポータルだけで追加した設定を残す前提にせず、必要設定は先にテンプレートへ反映する。

## 検証記録・参考資料

2026年10月14日：Bicep CLI 0.48.1で`main.bicep`と`prod.bicepparam`をエラー・警告なしでコンパイル。パラメーター検証には架空のID・通知先を使用した。生成されたARM定義について、ランタイム、Identity、HTTPS、Serverless、単一リージョン、7日バックアップ、DB／Storageキー認証禁止、パーティション、TTLなし、従量スループット、複合インデックス、予算通知等の15項目を確認した。Azure上のvalidate／what-if／デプロイは未実施。

- [FunctionsのIaCとFlex設定](https://learn.microsoft.com/en-us/azure/azure-functions/functions-infrastructure-as-code)
- [FunctionsのIdentity接続とホストStorage権限](https://learn.microsoft.com/azure/azure-functions/manage-connections)
- [Cosmos DBのデータプレーンRBAC](https://learn.microsoft.com/azure/cosmos-db/nosql/how-to-connect-role-based-access-control)
- [Cosmos DBの継続的バックアップ](https://learn.microsoft.com/en-us/azure/cosmos-db/continuous-backup-restore-introduction)
- [月額予算のリソース定義](https://learn.microsoft.com/en-us/azure/templates/microsoft.consumption/2024-08-01/budgets)
- [SWAと独立Functionsの連携制約](https://learn.microsoft.com/en-us/azure/static-web-apps/functions-bring-your-own)
