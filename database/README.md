# データベース定義・反映スクリプト

GitHub ActionsではDB資産の変更時に検証・コンパイルのみを自動実行する。Azureへの反映は手動実行。[pipelineの手順](../pipeline/README.md) を参照。

[DB設計](../spec/database-design.md)に基づく **Azure Cosmos DB for NoSQL** 用の資材。MongoDB用のコレクション定義・mongoshスクリプトではない。

## 構成

| ファイル | 用途 |
|---|---|
| `container-definition.json` | DB名・コンテナー名・パーティション・インデックスの共通定義 |
| `main.bicep` | 既存アカウント内のDBとコンテナーを作成・更新 |
| `Apply-Schema.ps1` | 対象を検証して差分確認／反映する実行入口 |
| `tests/Test-Schema.ps1` | Azureに接続しないスクリプト検証 |

作成対象はDatabase `rollpay`、Container `app`の二つ。パーティションは`/ownerKey`（Hash v2）、TTL無効、Unique Key追加なし。consistentインデックスで5パスを索引化し、履歴・ラウンド用の複合インデックス2種類を持つ。roster/game/round等はこの一つのコンテナーに保存するドキュメント種別で、個別のテーブルやコンテナーにはしない。

アカウント・リージョン・認証・バックアップ・RBACは[infra](../infra/README.md)で管理する。インフラ全体のテンプレートも共通JSONを参照するため、定義を変更するときはこのJSONを修正する。`database/main.bicep`の対象にアカウントや他のAzureリソースを含めない。

## 前提

- PowerShell 7、Azure CLI、Bicep CLI。
- 対象サブスクリプション・リソースグループ・Cosmos DBアカウントは作成済み。
- アカウントはNoSQL Serverless、Session整合性。MongoDB API・固定RUのアカウントは拒否する。
- 実行IDにDB・コンテナーの管理プレーン読取／書込とRGへのARMデプロイ権限が必要。Contributor等の利用可否を管理者が確認する。Cosmosのデータプレーン権限だけでは不足する。

キー取得・データプレーン接続は行わないため、キー認証が無効なアカウントにも使用できる。参加者データ・ゲーム・累積額の投入／更新は行わない。固定参加者の初期登録は別のデータ投入処理として後続で作成する。

## 差分確認と反映

リポジトリルートからPowerShell 7で実行する。プレースホルダーを実際の対象に置き換える。

```powershell
az login
az bicep install

$schemaTarget = @{
    SubscriptionId = '<対象サブスクリプションのGUID>'
    ResourceGroupName = '<対象リソースグループ名>'
    AccountName = '<作成済みCosmos DBアカウント名>'
}

# 既定はwhat-ifのみ。DBやコンテナーを変更しない。
./database/Apply-Schema.ps1 @schemaTarget

# 確認した対象に実反映する場合。
./database/Apply-Schema.ps1 @schemaTarget -Apply
```

全Azure CLI要求で明示したSubscriptionIdを使用し、CLIの既定サブスクリプションは変更しない。`-Apply -WhatIf`は反映を抑止するが、対象の読取検証は行う。既定の差分確認にもAzureログイン・読取権限が必要。

DB・コンテナーがなければ新規作成し、既存なら同じIDでインデックス定義を反映する。Incrementalモードを使い、無関係なリソースや既存ドキュメントを削除しない。DB作成とコンテナー更新をまたぐトランザクションではないため、失敗時にDBだけ作成済みになる場合はある。原因を解消して同じ指定で再実行する。

既存コンテナーのパーティション・Unique Keyが設計と違う場合やTTLが有効な場合は停止し、削除再作成・データ移行を自動実行しない。`defaultTtl=-1`もアイテム単位TTLが有効な設定なので拒否する。設計どおりのTTL無効はdefaultTtl未指定。既存インデックスは共通JSONの全体定義に置き換わるため、独自索引がある環境は差分を確認してから実行する。

事前確認と反映の間の他者の変更を排他する処理はない。`infra`配置とこのスクリプトは同時に実行せず、同じ対象の定義変更は一人ずつ行う。反映後もインデックス変換が継続する場合があるため、ポータル等で完了を確認してから依存するAPIクエリの疎通・RUを検証する。

## ローカル検証

```powershell
# Azureに接続しないテスト。CLIをテスト用の関数で置き換える。
pwsh -NoProfile -File database/tests/Test-Schema.ps1

# Bicepの構文・型検証。Azureリソースは作成しない。
New-Item -ItemType Directory -Path database/build -Force | Out-Null
az bicep build --file database/main.bicep --outfile database/build/main.json
```

`build/`と`*.local.json`はGit管理対象外。実IDや実名を含むファイルはコミットしない。

この資材の作成時点ではAzure上のwhat-if／反映／実データを使うクエリ検証は未実施。定義の反映と、アプリのドキュメント検証・ETag・TransactionalBatchの実装は別作業となる。

2026年10月15日：オフラインテスト通過。DB単独とインフラ全体のBicepをコンパイルし、共通定義の一致、DB単独の作成対象がDB・コンテナーの2リソースのみ、TTL・固定RU指定がないことを生成ARMで確認済み。

2026年10月16日：差分確認のJSON変換エラーを修正。Azure CLIのwhat-ifには`--output json`に加えて`--no-pretty-print`を指定し、表示用テキストではなくJSONを取得する。stderrもJSONから分離する。表示用テキスト・診断メッセージを模した回帰テストを追加。以前のスクリプトで`Error parsing NaN value`が出た場合は、修正版で同じコマンドを再実行する。この差分確認エラーによるDB作成・変更は発生しない。

参考：[コンテナーのARM/Bicep定義](https://learn.microsoft.com/en-us/azure/templates/microsoft.documentdb/2024-11-15/databaseaccounts/sqldatabases/containers)、[TTL設定](https://learn.microsoft.com/en-us/azure/cosmos-db/nosql/time-to-live)、[インデックス変更](https://learn.microsoft.com/en-us/azure/cosmos-db/index-policy)。
