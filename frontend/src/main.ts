import './style.css'

const app = document.querySelector<HTMLDivElement>('#app')

if (!app) {
  throw new Error('起動先の #app が見つかりません。')
}

app.innerHTML = `
  <main class="startup">
    <div class="dice-mark" aria-hidden="true">⚄</div>
    <p class="eyebrow">ROLL &amp; PAY</p>
    <h1>端数は、サイコロにおまかせ。</h1>
    <p>フロントエンドの開発環境を初期化しました。</p>
    <p class="status">画面と機能はこれから実装します。</p>
  </main>
`
