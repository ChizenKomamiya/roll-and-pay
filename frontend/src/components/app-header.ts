import { button, el, link } from './ui'

export function appHeader(loggedIn: boolean, logout: () => void) {
  const header = el('header', 'app-header', link('⚄ サイコロは絶対', loggedIn ? '/home' : '/login', 'brand'))
  if (loggedIn) header.append(button('ログアウト', logout, 'text-button'))
  return header
}
