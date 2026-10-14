import type { Page } from '../domain/types'
import { button, el, title } from '../components/ui'

export function loginPage(login: () => void): Page {
  const root = el('section', 'screen login-screen', el('div', 'hero-dice', '⚄'), title('LET THE DICE DECIDE.', '端数は、サイコロにおまかせ。', '小銭のやり取りをなくして、食事のあとの会計をすっきり。'))
  root.append(el('div', 'card', el('h2', '', 'まずはデモで試す'), el('p', 'muted', '仮の参加者で画面を試せます。記録はこのブラウザーに保存されます。'), button('デモをはじめる', login)), el('p', 'hint', 'Microsoftログイン・API接続は今後対応します。'))
  return { element: root }
}
