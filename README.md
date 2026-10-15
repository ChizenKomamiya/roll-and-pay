# roll-and-pay

食事や飲み会の会計で、割り勘の端数を負担する人をサイコロで決めるWebアプリです。

管理者がログインした一台のスマートフォンで参加者を選び、サイコロを振って敗者を決めます。会計総額から各自の負担額を計算し、ゲーム結果と負け額を保存します。過去の履歴や参加者ごとの累積負け額も確認できます。

画面はTypeScript、APIはC#のAzure Functions、データ保存はAzure Cosmos DBを使用する設計です。Azureのインフラ定義はBicepで管理します。現在はフロントエンドのデモ、APIのヘルスチェック・管理者認証・利用権限確認・DB接続・固定参加者取得、DBの初期登録ツール、Azureリソースを用意しています。フロントエンドからの本番認証・業務API接続は今後の対応です。

## ディレクトリ構成

単一リポジトリで、以下の構成を採用します。実装用ディレクトリは実装時に作成します。

```text
roll-and-pay/
├─ frontend/    # 画面：TypeScript、HTML、CSS、3D演出
├─ backend/     # API：C# Azure Functions、業務処理、DBアクセス
├─ database/    # DBの索引定義、初期データのサンプル、移行処理
├─ infra/       # Azureリソースの定義（Bicep）
├─ scripts/     # ビルド・デプロイ・DB初期登録などの実行スクリプト
├─ pipeline/    # 資産別のCI/CD実行スクリプトと設定手順
├─ .github/workflows/ # GitHub Actionsの実行入口
├─ docs/        # 要件・画面一覧・機能一覧
├─ spec/        # 基本設計・画面モック
├─ plan/        # 実装のグループ分け、ブランチ、依存関係、進捗
└─ README.md
```

DBを読み書きするC#コードは`backend/`、索引定義やデータ移行資材は`database/`に置きます。DB・コンテナーを含むAzureリソースの作成定義は`infra/`に置き、`scripts/`をビルド・配置・初期登録の実行入口とします。参加者の実名など、本番の個人データはリポジトリに含めません。

各ディレクトリの内部は、実装に合わせて必要な分だけ整理します。

## 関連資料

- [要件整理](docs/requirements.md)
- [画面一覧・機能一覧](docs/screens-and-features.md)
- [基本設計・画面モック](spec/README.md)
- [データベース定義・反映スクリプト](database/README.md)
- [バックエンド実装計画](plan/backend-implementation.md)
- [資産別のGitHub Actions・配置設定](pipeline/README.md)
