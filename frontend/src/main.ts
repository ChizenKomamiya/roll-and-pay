import { startApp } from './app'
import { readConfig } from './config'
import { createRuntime } from './services/runtime'
import { el, title } from './components/ui'
import './styles/base.css'
import './styles/components.css'

const root = document.querySelector<HTMLDivElement>('#app')
if (!root) throw new Error('起動先の #app が見つかりません。')
let disposed = false
let dispose: (() => void) | undefined
root.append(el('p', 'loading', '起動しています…'))
void (async () => {
  try {
    const runtime = await createRuntime(readConfig(import.meta.env))
    if (!disposed) dispose = startApp(root, runtime)
  } catch (error) {
    if (!disposed) root.replaceChildren(el('main', 'app-shell', title('PLEASE CHECK', '起動できませんでした。'), el('p', 'error', error instanceof Error ? error.message : '接続設定を確認してください。')))
  }
})()
if (import.meta.hot) import.meta.hot.dispose(() => { disposed = true; dispose?.() })
