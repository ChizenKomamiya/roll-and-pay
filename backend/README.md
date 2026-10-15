# Roll & Pay — バックエンド

C# / .NET 10 / Azure Functions 4.x Isolated Worker。G01（認証・HTTP共通処理）を実装。

## 現在の範囲

`GET /api/v1/me`（A01）は管理者のAPIアクセストークンで認証し、200 `{"authorized":true,"role":"administrator"}`を返す。本文・クエリの指定は認証後に400で拒否する。A01はDBに接続しない。

`GET /api/v1/health`は認証なしで200と次のJSONを返す。

```json
{"status":"ok"}
```

`Cache-Control: no-store`を付与する。APIプロセスへの到達確認用で、DB接続やEntra IDの正常性を確認するreadinessチェックではない。環境名・バージョン・接続情報は返さない。

ゲームAPI、Cosmos DB接続、フロントエンドとの通信は後続グループで実装する。ヘルスチェックは匿名の運用エンドポイントで、全業務APIを管理者限定にする設計とは区別する。

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
```

healthの200、JSONの`status: ok`、`Cache-Control: no-store`、トークンなしmeの401を確認する。有効な管理者トークンならmeは200。終了は起動ターミナルでCtrl+C。NuGetパッケージの初回取得にはネットワーク接続が必要。

## テスト

```powershell
cd backend
dotnet test tests/RollAndPay.Api.Tests.csproj --configuration Release
```

テスト専用RSA鍵による署名付きJWTとDIで差し替えた署名メタデータを使い、認証・認可、期限・発行者・aud・署名・スコープの拒否、鍵更新、OIDC取得・キャッシュ・障害、HTTP共通エラー、業務処理の呼出し停止、A01、ヘルスチェック、OPTIONSを検証する。本番コードの検証手順を通り、実テナントやDBは使用しない。テストは発行物から除外し、CIの`pipeline/backend/Build.ps1`でも実行する。

2026年10月16日：49テスト成功、Releaseビルド・発行成功、発行物へのローカル設定・テスト資材の混入なし。Core Toolsの実ホストでhealth 200・me 401・OPTIONS 204、ローカルCORSのOrigin許可・拒否、401での応答ヘッダー公開を確認。実テナントの管理者トークンによる疎通、Azure上のOrigin許可・拒否とプリフライトは未検証。Azureへの配置はこの作業に含まない。

## ファイル構成

- `Program.cs`：FunctionsホストとHTTP連携の初期化。
- `Functions/HealthFunction.cs`：ヘルスチェック。
- `Functions/MeFunction.cs`／`OptionsFunction.cs`：A01とプリフライト。
- `Authentication/`：認証設定、JWT検証と管理者認可。
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
