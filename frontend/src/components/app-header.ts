import { button, el, link } from './ui'

export function appHeader(loggedIn: boolean, logout: () => void) {
  const header = el('header', 'app-header', link('⚄ Roll & Pay', loggedIn ? '/home' : '/login', 'brand'))
  if (loggedIn) header.append(button('ログアウト', logout, 'text-button'))
  return header
}
