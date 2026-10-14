import { appHeader } from './components/app-header'
import { actions, button, el, link, title } from './components/ui'
import type { PageContext } from './components/ui'
import type { Page } from './domain/types'
import { dicePage } from './pages/dice'
import { historyDetailPage } from './pages/history-detail'
import { historyPage } from './pages/history'
import { homePage } from './pages/home'
import { loginPage } from './pages/login'
import { participantsPage } from './pages/participants'
import { paymentPage } from './pages/payment'
import { navigate, parseRoute } from './router'
import { createMockService, STORAGE_KEY } from './services/mock-service'
import { state } from './state'

const SESSION_KEY = 'roll-and-pay.demo.session'

export function startApp(root: HTMLElement) {
  const service = createMockService({ getItem: key => localStorage.getItem(key), setItem: (key, value) => localStorage.setItem(key, value) })
  let page: Page | undefined, generation = 0
  let signedIn = false
  try { signedIn = sessionStorage.getItem(SESSION_KEY) === 'active' } catch { /* A session can still run without persisting the sign-in. */ }
  function setSignIn(value: boolean) {
    signedIn = value
    try { if (value) sessionStorage.setItem(SESSION_KEY, 'active'); else sessionStorage.removeItem(SESSION_KEY) } catch { /* Storage errors for game data are handled separately by the service. */ }
  }
  const login = () => { setSignIn(true); navigate('/home', true) }
  const logout = () => { setSignIn(false); state.paymentInputs.clear(); state.selectedIds.clear(); navigate('/login', true) }

  async function render() {
    const current = ++generation
    page?.dispose?.(); page = undefined
    const route = parseRoute(location.hash)
    if (!signedIn && route.page !== 'login') { navigate('/login', true); return }
    if (signedIn && route.page === 'login') { navigate('/home', true); return }
    const shell = el('div', 'app-shell', appHeader(signedIn, logout), el('div', 'demo-banner', 'デモモード · 記録はこのブラウザーに保存'))
    const main = el('main'); shell.append(main); root.replaceChildren(shell)
    main.append(el('p', 'loading', '読み込んでいます…'))
    const context: PageContext = { service, isCurrent: () => current === generation, refresh: () => { void render() } }
    try {
      let result: Page
      switch (route.page) {
        case 'login': result = loginPage(login); break
        case 'home': result = await homePage(context); break
        case 'participants': result = await participantsPage(context); break
        case 'history': result = await historyPage(context); break
        case 'deleted': result = { element: el('section', 'screen', title('LOSS HISTORY', '履歴を削除しました。'), el('p', '', '履歴一覧と累積負け額を更新しました。'), link('履歴一覧へ →', '/history', 'button primary'), link('ホームへ →', '/home')) }; break
        case 'dice': result = await dicePage(context, route.id); break
        case 'payment': result = await paymentPage(context, route.id); break
        case 'result': result = await paymentPage(context, route.id, true); break
        case 'detail': result = await historyDetailPage(context, route.id); break
        case 'delete': result = await historyDetailPage(context, route.id, true); break
        default: result = { element: el('section', 'screen', title('PAGE NOT FOUND', '画面が見つかりません。'), link('ホームへ →', '/home')) }
      }
      if (!context.isCurrent()) { result.dispose?.(); return }
      page = result; main.replaceChildren(result.element)
      const heading = main.querySelector('h1')
      document.title = `Roll & Pay${heading ? ` | ${heading.textContent}` : ''}`
      window.scrollTo(0, 0); heading?.focus({ preventScroll: true })
    } catch (reason) {
      if (!context.isCurrent()) return
      const errorPage = el('section', 'screen', title('PLEASE CHECK', '表示できませんでした。'), el('p', 'error', reason instanceof Error ? reason.message : '再度お試しください。'), button('再読み込み', () => { void render() }), link('ホームへ →', '/home'))
      const details = el('details', 'demo-options', el('summary', '', 'デモデータを初期化'))
      const action = actions(context, details)
      details.append(el('p', '', 'このブラウザーのデモ記録を破棄し、サンプル4件に戻します。'), button('確認してサンプルに戻す', () => { void action.run(async () => { await service.resetDemo(); navigate('/home', true) }) }, 'button danger'))
      errorPage.append(details); main.replaceChildren(errorPage)
    }
  }
  const refresh = () => { void render() }
  const onStorage = (event: StorageEvent) => { if (event.key === STORAGE_KEY || event.key === null) refresh() }
  window.addEventListener('hashchange', refresh); window.addEventListener('storage', onStorage)
  refresh()
  return () => { generation++; page?.dispose?.(); window.removeEventListener('hashchange', refresh); window.removeEventListener('storage', onStorage) }
}
