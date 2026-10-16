import type { Page } from '../domain/types'
import { button, el, title } from '../components/ui'

export function loginPage(login: () => Promise<void>, mode: 'demo' | 'api', message?: string): Page {
  const root = el('section', 'screen login-screen', el('div', 'hero-dice', '⚄'), title('LET THE DICE DECIDE.', '端数は、サイコロにおまかせ。', '小銭のやり取りをなくして、食事のあとの会計をすっきり。'))
  const error = el('p', 'error', message ?? ''); error.setAttribute('role', 'alert'); error.hidden = !message
  const submit = button(mode === 'api' ? 'Microsoftでログイン' : 'デモをはじめる', () => {
    submit.disabled = true; error.hidden = true
    void login().catch(reason => { if (!disposed) { error.textContent = mode === 'api' && !(reason instanceof Error && /利用できません/.test(reason.message)) ? 'ログインを完了できませんでした。アカウントとポップアップの許可を確認し、再度お試しください。' : reason instanceof Error ? reason.message : 'ログインできませんでした。'; error.hidden = false } }).finally(() => { if (!disposed) submit.disabled = false })
  })
  let disposed = false
  root.append(el('div', 'card', el('h2', '', mode === 'api' ? '管理者アカウントでログイン' : 'まずはデモで試す'), el('p', 'muted', mode === 'api' ? '登録済みのMicrosoftアカウントで利用権限を確認します。' : '仮の参加者で画面を試せます。記録はこのブラウザーに保存されます。'), submit, error))
  return { element: root, dispose: () => { disposed = true } }
}
