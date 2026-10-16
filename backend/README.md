# Roll & Pay — バックエンド

C# / .NET 10 / Azure Functions 4.x Isolated Worker。G01（認証・HTTP共通処理）とG02（DB接続・参加者取得）を実装。

## 現在の範囲

`GET /api/v1/me`（A01）は管理者のAPIアクセストークンで認証し、200 `{"authorized":true,"role":"administrator"}`を返す。本文・クエリの指定は認証後に400で拒否する。A01はDBに接続しない。

`GET /api/v1/participants`（A02）は本人のrosterをPoint Readし、有効な参加者をsortOrder→participantIdの順で返す。応答は`{"participants":[{"participantId":"p001","displayName":"参加者A","sortOrder":1}]}`。有効者が0〜1人でも200。本文・クエリは400、データ未配置・破損・読取セッションを満たせない場合は503。

`GET /api/v1/health`は認証なしで200と次のJSONを返す。

```json
{"status":"ok"}
```

`Cache-Control: no-store`を付与する。APIプロセスへの到達確認用で、DB接続やEntra IDの正常性を確認するreadinessチェックではない。環境名・バージョン・接続情報は返さない。

ゲームAPI、フロントエンドとの通信は後続グループで実装する。ヘルスチェックは匿名の運用エンドポイントで、全業務APIを管理者限定にする設計とは区別する。

## 認証・HTTP共通処理

全HTTP関数にWorkerミドルウェアを適用し、GETのHealth関数とOPTIONSだけを明示的に認証対象から除外する。`Authorization: Bearer <API access token>`が必須。Microsoft IdentityModelでテナント固有のHTTPS OpenID Connectメタデータ・JWKSを取得し、キャッシュと署名鍵更新を行う。RS256署名、発行者、API Client IDのaud、有効期限（許容時刻差30秒）、v2トークンを検証した後、tid・管理者oid・SPAのazp・委任scpを確認する。メールアドレスやクライアント指定ユーザーIDで認可しない。IDトークン、Graph向けトークン、アプリ単独トークンは許可しない。

設定名は既存の`infra/resources.bicep`と共通。`local.settings.json`のValuesでは次を指定する。IDはすべて空でないGUID、APIとSPAは別登録とする。

| 設定 | 値 |
|---|---|
| `Auth__TenantId` | 対象のEntraテナントID |
| `Auth__ApiClientId` | APIアプリのClient ID（v2トークンのaud） |
| `Auth__AllowedSpaClientId` | 許可するSPAアプリのClient ID |
| `Auth__AdministratorObjectId` | 対象テナント内の管理者ユーザーObject ID |
| `Auth__RequiredScope` | `access_as_user` |

クライアントシークレットは不要。ローカルでも実際のEntra APIアクセストークンを用いる。認証を無効化する環境設定や開発用トークンは実装しない。設定不備や初回メタデータ取得失敗は業務APIを503（`Retry-After: 5`）で停止させ、ヘルスチェックは維持する。メタデータHTTP取得のタイムアウトは10秒。設定変更後はプロセスを再起動する。

欠落・不正・期限切れは401 `UNAUTHENTICATED`と`WWW-Authenticate: Bearer`、有効トークンの管理者・SPA・スコープ等の不足は403 `FORBIDDEN`。認証・認可に失敗した要求は関数本体やDB処理を呼ばない。エラーはAPI定義の`error.code/message/details/requestId`を返し、requestIdは要求ごとにサーバーで生成する。内部例外・トークン・個人情報を応答やアプリログに出さない。

全応答に`Cache-Control: no-store`と`Access-Control-Expose-Headers: ETag, Location, Idempotency-Replayed, Retry-After, X-Read-Session`を設定する。Originの許可と`Access-Control-Allow-Origin`はFunctionsホストのCORS設定に任せ、APIで重複設定しない。本番は既存BicepのSWA Origin完全一致を維持する。ローカル設定例のHost.CORSは`http://localhost:5173`のみ。実際の開発Originに合わせてローカルファイルを編集する。`OPTIONS /api/v1/{*path}`は認証・業務処理を呼ばず204を返し、GET/POST/DELETE/OPTIONSとAPI定義の要求ヘッダーを提示する。CORSは認証の代わりにはならない。

## Cosmos DB接続と読取セッション

既存インフラの`Cosmos__Endpoint`（HTTPSアカウントURL）、`Cosmos__DatabaseName`、`Cosmos__ContainerName`を読み込む。設定例は`rollpay/app`。APIにDB・コンテナー・初期データの作成処理はない。DB設定や接続が不完全でもhealth・meは利用でき、A02だけが503になる。

本番はシステム割当Managed Identity、Development環境はAzure CLIのログインIDで認証する。キー・接続文字列・クライアントシークレットは使わない。ローカルで実DBを読む場合は`az login --tenant <対象テナントID>`し、接続先を明示する。API起動時には接続せず、初回のDB利用時にクライアントを作り、Functionsプロセス内で再利用する。既定の本番接続先は設けない。

全Point Readで、検証済みtid・oidをGUIDの小文字D形式に正規化した`<tid>:<oid>`をPartitionKeyに指定する。クエリや追加ヘッダーのownerKeyを採用しない。rosterのid/type/schemaVersion/ownerKey、参加者ID・重複・人数・名前・順序・UTC日時も検証する。APIが返すのは公開フィールドだけで、内部ETagは公開しない。

SDKはGateway接続・Session整合性。SDK要求のタイムアウト5秒、429の追加再試行は最大2回・合計待機3秒、A02のDB処理全体に10秒の上限を置く。429は`RATE_LIMITED`、タイムアウト・データ未配置・権限不足・セッション未達は`TEMPORARILY_UNAVAILABLE`に変換し、Retry-After秒を返す。接続先・SDK内部診断・参加者名をエラーやログに出さない。

`X-Read-Session`はSDKのSessionTokenをUTF-8→base64url（パディングなし）にした不透明値。認証後に単一ヘッダー・8 KiB以下・正規base64url・正しいUTF-8・制御文字なしを確認し、不正なら400 `INVALID_READ_SESSION`。A02は復元値をSDKのSessionTokenへ渡し、成功読取で得た最新値を応答に返す。別インスタンスへの次要求でもこの値を渡す。無効なSDKセッションは400、セッションを満たせない読取は503とし、セッションなしで読み直して成功扱いにはしない。DB未使用のA01では応答セッションを省略する。

初期3ドキュメントの登録とローカルエミュレーター統合検証は[databaseの手順](../database/README.md)を参照。本番への登録・Azure配置はコードのPR作成とは別作業。

## 初回の環境準備（Windows）

必要なもの：.NET 10 SDK、.NET 10に対応するAzure Functions Core Tools v4。

通常のPowerShellでインストールする場合：

```powershell
winget install --exact --id Microsoft.DotNet.SDK.10
winget install --exact --id Microsoft.Azure.FunctionsCoreTools
```

インストール後はターミナルを開き直す。VS CodeのターミナルならVS Codeも再起動する。

```powershell
dotnet --list-sdks
func --version
```

`global.json`は10.0.100以上の安定版.NET 10 SDKを選ぶ（同じメジャー・マイナー内で最新feature bandへロールフォワード）。SDKはFunctionsランタイムとは別に必要。

## ビルド・ローカル起動

リポジトリのルートから：

```powershell
cd backend
Copy-Item local.settings.example.json local.settings.json
dotnet restore RollAndPay.Api.csproj
dotnet build RollAndPay.Api.csproj --no-restore
dotnet run
```

設定のコピーは初回だけ。上記のAuth設定を実際のIDに置き換える。`local.settings.json`はGit管理・発行対象から除外している。HTTPトリガーの起動・匿名ヘルスチェックにAzureへのログイン、Cosmos DB、Azuriteは不要。認証成功の確認にはMSAL等で取得した対象APIの委任アクセストークンとEntraへのメタデータ取得通信が必要。Core ToolsはStorage未設定の内部ヘルス警告を出すことがあるが、HTTPエンドポイントのヘルスチェックとは別である。

別ターミナルで確認する：

```powershell
curl.exe -i http://localhost:7071/api/v1/health
curl.exe -i http://localhost:7071/api/v1/me
# MSALでAPI向けトークンを取得し、$accessTokenに保持した場合：
curl.exe -i http://localhost:7071/api/v1/me -H "Authorization: Bearer $accessToken"
curl.exe -i http://localhost:7071/api/v1/participants -H "Authorization: Bearer $accessToken"
```

healthの200、JSONの`status: ok`、`Cache-Control: no-store`、トークンなしmeの401を確認する。有効な管理者トークンならmeは200。終了は起動ターミナルでCtrl+C。NuGetパッケージの初回取得にはネットワーク接続が必要。

## テスト

```powershell
cd backend
dotnet test tests/RollAndPay.Api.Tests.csproj --configuration Release
```

オフラインテストではテスト専用RSA鍵によるJWTを使い、認証・認可、A01・A02、エラー形式、DBの未呼出し、所有者分離、セッション継承、初期登録の検証と3件Createバッチを確認する。SDKのContainer等をモックするテストは実DBの原子性の証明には含めない。localhostのCosmosエミュレーター専用の統合2件は通常はスキップし、[databaseの実行入口](../database/README.md)から明示的に有効化する。テストの差替えはDI内だけで行い、本番の認証回避設定は設けない。テストと登録ツールはAPIの発行物から除外し、CIの`pipeline/backend/Build.ps1`でも確認する。

2026年10月16日：49テスト成功、Releaseビルド・発行成功、発行物へのローカル設定・テスト資材の混入なし。Core Toolsの実ホストでhealth 200・me 401・OPTIONS 204、ローカルCORSのOrigin許可・拒否、401での応答ヘッダー公開を確認。実テナントの管理者トークンによる疎通、Azure上のOrigin許可・拒否とプリフライトは未検証。Azureへの配置はこの作業に含まない。

G02追加後：オフライン103テスト・Release発行成功、実Functionsホストでhealth 200・participants 401・OPTIONS 204を確認。エミュレーター未導入のため統合2件はスキップし、実Cosmosでの読取・一括登録・SessionToken継承・Managed Identity/RBACは未検証。本番へのデータ登録・Azure配置は未実施。

## ファイル構成

- `Program.cs`：FunctionsホストとHTTP連携の初期化。
- `Functions/HealthFunction.cs`：ヘルスチェック。
- `Functions/MeFunction.cs`／`OptionsFunction.cs`：A01とプリフライト。
- `Functions/ParticipantsFunction.cs`：A02。
- `Authentication/`：認証設定、JWT検証と管理者認可。
- `Data/`：Cosmosクライアント・保存モデル・所有者識別・rosterのPoint Read。
- `Http/`：全HTTP関数に適用するWorkerミドルウェア、共通パイプラインとエラー。
- `tests/`：認証・HTTPの自動テスト（発行対象外）。
- `RollAndPay.Api.csproj`：SDKと依存パッケージ。
- `host.json`：Functionsホスト設定。
- `global.json`：.NET SDKの選択。
- `local.settings.example.json`：秘密情報を含まないローカル設定例。

設計は[API定義](../spec/api-design.md)・[インフラ設計](../spec/infrastructure.md)を参照。

## GitHub Actionsでの配置

バックエンド資産がmainに入ったときだけ、.NET 10のテスト・発行後に既存Flex Consumptionへ自動配置する。フロントエンドとは別のワークフロー。ローカル設定ファイル・設定例・テストは発行物から除外する。GitHubとAzureのOIDC準備、Repository Variablesは [pipelineの手順](../pipeline/README.md) を参照。配置対象は上記エンドポイントと認証・HTTP共通処理。

参考：[Isolated Worker公式ガイド](https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide)、[Core Tools](https://github.com/Azure/azure-functions-core-tools)、[HTTPトリガーのローカル設定](https://learn.microsoft.com/en-us/azure/azure-functions/functions-develop-vs?pivots=isolated)。
