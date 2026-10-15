# 初回デプロイの確認記録

確認日：2026年10月15日（日本時間）

ユーザーが`az deployment sub create`を実行。CLIに`DeploymentNotFound`が表示されたが、Azure上では`rollpay-prod`が作成され、リソースの配置が進行していた。再配置は行わず、読み取りで状態を確認し、親・子デプロイの成功を確認した。CLI表示エラーの正確な原因は未確定。

| 対象 | 確認結果 |
|---|---|
| 親デプロイ `rollpay-prod` | `Succeeded`、エラーなし |
| 子デプロイ `rollpay-production-resources` | `Succeeded` |
| 権限モジュール `rollpay-data-access` | `Succeeded` |
| Static Web Apps | Free、East Asia、プレビュー無効 |
| Functionsプラン | FC1 / FlexConsumption |
| Function App | Running、.NET Isolated 10.0、HTTPSのみ |
| Flexスケール | 2,048 MB、最大40、Always readyなし |
| Function Identity / 配布Storage認証 | SystemAssigned / SystemAssignedIdentity |
| CORS | 当該SWAのHTTPS Origin 1件のみ、Credentialsなし |
| Function TLS / FTP | Web・SCMとも最小TLS 1.2、FTPS無効 |
| Storage | Standard LRS、HTTPSのみ、最小TLS 1.2、共有キー認証・匿名Blob公開とも無効 |
| Cosmos DB | Serverless、Session、ローカルキー認証無効、継続バックアップ7日 |
| DBコンテナー | `rollpay/app`、`/ownerKey`、Hash v2、consistentインデックス、TTL設定なし |
| DBデータロール | FunctionsのIdentityに、`rollpay` DBスコープのBuilt-in Data Contributor |
| Storageロール | 同じFunctionsのIdentityに、ホストStorageスコープのStorage Blob Data Owner |
| 監視・予算 | Application Insights、Log Analytics、月額予算のリソース作成成功。通知の実配信・ログ収集は未検証 |

確認時のAzure CLIは2.61.0。`az functionapp show`では一部項目がnullとなったため、ARM API `2024-11-01`のGETでFlex設定・HTTPS・CORS・TLSを直接確認した。旧CLIの出力だけで設定未反映とは判断しない。

同日、Entra SPA登録のリダイレクトURIに、デプロイ済みSWAのHTTPS URL（末尾スラッシュ付き）を追加した。既存の`http://localhost:5173/`を保持し、Graphの再読取りで両方の登録を確認。APIの権限設定は変更していない。Git対象外の`infra/entra.local.json`にも登録済みURIを反映した。

この記録はリソース配置と上記設定の確認まで。API・画面のコード配置、Bearer認証、実データの読み書き、ログの個人情報除外、予算通知の受信、バックアップ復元は未検証。次にアプリ実装・配置後の確認を行う。
