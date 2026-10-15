# Roll & Pay — バックエンド

C# / .NET 10 / Azure Functions 4.x Isolated Workerのベースライン。

## 現在の範囲

`GET /api/v1/health`のみ実装する。認証なしで200と次のJSONを返す。

```json
{"status":"ok"}
```

`Cache-Control: no-store`を付与する。APIプロセスへの到達確認用で、DB接続やEntra IDの正常性を確認するreadinessチェックではない。環境名・バージョン・接続情報は返さない。

ゲームAPI、認証ミドルウェア、Cosmos DB接続、フロントエンドとの通信は後続で実装する。ヘルスチェックは匿名の運用エンドポイントで、全業務APIを管理者限定にする設計とは区別する。Azure配置のワークフローと必要な設定は後述する。

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
func start --port 7071
```

設定のコピーは初回だけ。`local.settings.json`はGit管理・発行対象から除外している。現時点はHTTPトリガーのみなので、ローカル起動にAzureへのログイン、Cosmos DB、Azuriteは不要。ストレージを利用するトリガー等を追加するときに設定を追加する。本番Functions用Storageの設計は別途必要。

別ターミナルで確認する：

```powershell
curl.exe -i http://localhost:7071/api/v1/health
```

200、JSONの`status: ok`、`Cache-Control: no-store`を確認する。終了は起動ターミナルでCtrl+C。NuGetパッケージの初回取得にはネットワーク接続が必要。

## ファイル構成

- `Program.cs`：FunctionsホストとHTTP連携の初期化。
- `Functions/HealthFunction.cs`：ヘルスチェック。
- `RollAndPay.Api.csproj`：SDKと依存パッケージ。
- `host.json`：Functionsホスト設定。
- `global.json`：.NET SDKの選択。
- `local.settings.example.json`：秘密情報を含まないローカル設定例。

設計は[API定義](../spec/api-design.md)・[インフラ設計](../spec/infrastructure.md)を参照。

## GitHub Actionsでの配置

バックエンド資産がmainに入ったときだけ、.NET 10の発行後に既存Flex Consumptionへ自動配置する。フロントエンドとは別のワークフロー。ローカル設定ファイル・設定例は発行物から除外する。GitHubとAzureのOIDC準備、Repository Variablesは [pipelineの手順](../pipeline/README.md) を参照。現時点の配置対象は上記ヘルスチェックのみ。

参考：[Isolated Worker公式ガイド](https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide)、[Core Tools](https://github.com/Azure/azure-functions-core-tools)、[HTTPトリガーのローカル設定](https://learn.microsoft.com/en-us/azure/azure-functions/functions-develop-vs?pivots=isolated)。
