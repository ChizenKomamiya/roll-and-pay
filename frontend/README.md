# Roll & Pay — フロントエンド

Vite + Vanilla TypeScriptで初期化。React等のUIフレームワークは使用しない。

## 開発

Node.js 22.12以降とpnpm 11.25.0を使用する。今回の検証環境はNode.js 24.19.0。

リポジトリのルートから：

```powershell
cd frontend
pnpm install --frozen-lockfile
pnpm dev
```

ターミナルに表示されるURLをブラウザーで開く。開発サーバーは既定ではローカル向け。

## 確認・ビルド

```powershell
pnpm typecheck
pnpm build
pnpm preview
```

`build` はTypeScriptの確認後、静的ファイルを `dist/` に出力する。`preview` はビルド済みファイルのローカル確認用。

## 構成と現在の範囲

- `index.html`：日本語ページの入口。
- `src/main.ts`：起動画面。
- `src/style.css`：起動画面のスタイル。
- `tsconfig.json`：TypeScript strictを有効化。
- `pnpm-lock.yaml`：依存バージョンの固定。

今回は開発・型チェック・ビルドの土台まで。画面機能、認証、API通信、Three.js・GSAPは後続で実装する。

確定した画面モックは [spec/mockups/index.html](../spec/mockups/index.html)、表示ルールは [画面設計](../spec/screen-mockups.md) を参照する。

参考：[Vite公式ガイド](https://vite.dev/guide/)
