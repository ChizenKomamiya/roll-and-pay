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
| `participants.example.json` | 実名を含まない参加者入力例 |
| `seed/` | .NET初期登録ツール。roster/control/lossTotalsの一括Create |
| `tests/Test-CosmosIntegration.ps1` | localhostエミュレーター専用の統合テスト実行入口 |

作成対象はDatabase `rollpay`、Container `app`の二つ。パーティションは`/ownerKey`（Hash v2）、TTL無効、Unique Key追加なし。consistentインデックスで5パスを索引化し、履歴・ラウンド用の複合インデックス2種類を持つ。roster/game/round等はこの一つのコンテナーに保存するドキュメント種別で、個別のテーブルやコンテナーにはしない。

アカウント・リージョン・認証・バックアップ・RBACは[infra](../infra/README.md)で管理する。インフラ全体のテンプレートも共通JSONを参照するため、定義を変更するときはこのJSONを修正する。`database/main.bicep`の対象にアカウントや他のAzureリソースを含めない。

## 前提

- PowerShell 7、Azure CLI、Bicep CLI。
- 対象サブスクリプション・リソースグループ・Cosmos DBアカウントは作成済み。
- アカウントはNoSQL Serverless、Session整合性。MongoDB API・固定RUのアカウントは拒否する。
- 実行IDにDB・コンテナーの管理プレーン読取／書込とRGへのARMデプロイ権限が必要。Contributor等の利用可否を管理者が確認する。Cosmosのデータプレーン権限だけでは不足する。

`Apply-Schema.ps1`はキー取得・データプレーン接続を行わないため、キー認証が無効なアカウントにも使用できる。参加者データ・ゲーム・累積額の投入／更新は行わない。固定参加者の初期登録は次の専用ツールで別途行う。

## 固定参加者の初期登録（G02）

`seed/RollAndPay.Seed.csproj`は既存のDB・コンテナーへ、同一ownerKeyの`roster`・`control`・`lossTotals`を一つのTransactionalBatchでCreateする。Upsert/Replaceは使わず、どれか一件でも既存なら全件失敗とし、既存データを上書きしない。controlはactiveGameId=null、累積額は無効者も含め全員`"0"`で初期化する。既存参加者の変更や集計再構築には使わない。

必要なものは.NET 10 SDKとAzure CLI。配置専用のIDで対象テナントにログインし、対象コンテナーのデータプレーン権限（Cosmos DB Built-in Data Contributor等）を別途用意する。通常のAzure Contributorだけではデータを作成できない。APIのManaged Identityを借用せず、キー・シークレットを取得しない。ツールはAzureCliCredentialを使い、指定したTenantIdでトークンを要求する。

1. `participants.example.json`を`database/participants.local.json`へコピーし、参加者を編集する。`*.local.json`はGit管理対象外。実名はこのローカルファイルにだけ保存し、PRやログに貼り付けない。
2. テナントID・対象テナント内の管理者Object IDをAPIのAuth設定と照合する。ownerKeyは両者を小文字GUIDでつないだ`<tid>:<oid>`。対象Endpoint・DB・Containerも確認する。
3. 既定のローカル検証を実行する。実行結果は対象・ownerKey・人数・作成件数のみを表示し、参加者名やJSON本文は出力しない。Azure接続・書込は行わない。

```powershell
# リポジトリルートから。復元はAPIプロジェクトを直接指定する。
dotnet restore backend/RollAndPay.Api.csproj
Copy-Item database/participants.example.json database/participants.local.json

$seedArgs = @(
  '--endpoint', 'https://<account>.documents.azure.com:443/',
  '--database', 'rollpay', '--container', 'app',
  '--tenant-id', '<対象テナントGUID>',
  '--administrator-object-id', '<対象テナント内の管理者ユーザーGUID>',
  '--participants-file', 'database/participants.local.json'
)
dotnet run --project database/seed/RollAndPay.Seed.csproj -- @seedArgs
```

入力は`{"participants":[...]}`。0〜100人、participantIdは英数字・ハイフン・アンダースコア1〜40文字で重複なし、displayNameは空白のみを除く1〜50文字、sortOrderは非負整数、isActiveは真偽値。全項目必須、未知・重複JSONフィールドを拒否し、ファイルは64 KiB以下。inactiveの参加者も検証・初期集計の対象となる。入力からownerKeyや累積額を指定できない。

4. 本番登録を別途実行する際は、配置用IDのログイン先・表示された対象とownerKeyを確認し、同じ引数に`--apply --confirm-owner '<tid>:<oid>'`を追加する。このPR作成作業では実環境への登録は実行しない。

```powershell
az login --tenant '<対象テナントGUID>'
dotnet run --project database/seed/RollAndPay.Seed.csproj -- @seedArgs `
  --apply --confirm-owner '<小文字の対象テナントGUID>:<小文字の管理者ユーザーGUID>'
```

成功の終了コードは0、既存データとの競合は3、バッチ失敗は4、入力・認証・通信等の失敗は2。再実行もCreateだけなので上書きしない。通信タイムアウトはコミット未成立の証明にはならない。応答が不明な場合は3件を同じownerKeyで確認し、既存なら登録し直さず原因を調査する。途中の一件だけを削除する復旧や再作成をツールに組み込まない。

5. APIと同じAuth設定の管理者APIアクセストークンで`GET /api/v1/participants`を確認する。有効者だけが表示順で返り、`X-Read-Session`が付くことを確認する。DB作成・参加者初期登録はAPI起動時に自動実行しない。

## Cosmos統合検証環境

Windowsネイティブの[Azure Cosmos DBエミュレーター](https://learn.microsoft.com/en-us/azure/cosmos-db/emulator-windows-arguments)を使う。Docker/WSLは不要。エミュレーターを別途導入・起動し、ローカル証明書を信頼した上で次を実行する。証明書検証を無効化する処理はない。API本体にはエミュレーターのキー認証経路を追加しない。

```powershell
$env:ROLLPAY_EMULATOR_KEY = '<ローカルエミュレーターのキー>'
./database/tests/Test-CosmosIntegration.ps1 -Endpoint https://localhost:8081/
Remove-Item Env:ROLLPAY_EMULATOR_KEY
```

統合テストはHTTPSのloopback以外を拒否し、固有名の`rollpay-g02-test-<GUID>`、`rollpay-g03-test-<GUID>`、`rollpay-g04-test-<GUID>`、`rollpay-g09-test-<GUID>` DBと`/ownerKey`コンテナーを作成する。終了時は作成したテストDBだけを削除する。G02は初期3件の同時コミット、各項目が既存の場合の全件ロールバックとETag維持、所有者分離とSessionToken継承を検証する。G03は全ゲーム状態の復元、未完了ポインター、削除・所有者境界、および複数読取の途中で実TransactionalBatchが更新した場合の整合性を検証する。G04は同キー／別キーの同時作成、rosterのETag競合による全件ロールバック、コミット後応答断の再生、別クライアントへのセッション継承を検証する。G09は既存のコンテナー定義の複合インデックスを使い、同日時の境界ページング、削除された境界行、当時名、roundなしの詳細、所有者分離と別クライアントへの削除後セッション継承を検証する。実Azure環境は作成・使用しない。エミュレーターのキー認証は本番のManaged Identity/RBAC検証を代替しないため、Azureでの疎通・権限確認は別途必要。

通常の`dotnet test backend/tests/RollAndPay.Api.Tests.csproj`はエミュレーター統合9件（G02・G03・G09各2件、G04の3件）を理由付きでスキップする。上の実行入口は環境変数で明示的に有効化するため、未起動・未接続を成功扱いにしない。Backend CIは全オフラインテストと発行除外を、Database CIは初期登録ツールのテスト・既存スキーマ検証・Bicepコンパイルを実行する。CIから初期データは登録しない。

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

Azure上のwhat-if／反映／実データを使うクエリ検証の実施状況は個別の作業記録を参照。G02でアプリのroster検証と初期登録のTransactionalBatch、G03でゲームの公開ETagと読取の整合性検証、G04でゲーム作成の条件付きバッチと成功操作記録、G06で抽選・次ラウンド作成の条件付き3操作バッチを追加。G06の実Cosmos統合3件はエミュレーター未導入のため未実施。金額確定・中止・履歴削除は後続グループで実装する。

2026年10月16日（G02）：オフライン103テスト、初期登録ツールの例示データによる無接続・無書込実行、Release発行とローカル設定・テスト・登録ツールの発行除外、スキーマのオフライン検証とBicepコンパイルが成功。エミュレーターはこの端末に未導入のため、統合2件は未実施（スキップ）。実環境への参加者登録・AzureでのRBAC確認は未実施。

2026年10月15日：オフラインテスト通過。DB単独とインフラ全体のBicepをコンパイルし、共通定義の一致、DB単独の作成対象がDB・コンテナーの2リソースのみ、TTL・固定RU指定がないことを生成ARMで確認済み。

2026年10月16日：差分確認のJSON変換エラーを修正。Azure CLIのwhat-ifには`--output json`に加えて`--no-pretty-print`を指定し、表示用テキストではなくJSONを取得する。stderrもJSONから分離する。表示用テキスト・診断メッセージを模した回帰テストを追加。以前のスクリプトで`Error parsing NaN value`が出た場合は、修正版で同じコマンドを再実行する。この差分確認エラーによるDB作成・変更は発生しない。

参考：[コンテナーのARM/Bicep定義](https://learn.microsoft.com/en-us/azure/templates/microsoft.documentdb/2024-11-15/databaseaccounts/sqldatabases/containers)、[TTL設定](https://learn.microsoft.com/en-us/azure/cosmos-db/nosql/time-to-live)、[インデックス変更](https://learn.microsoft.com/en-us/azure/cosmos-db/index-policy)。
