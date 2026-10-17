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
import { STORAGE_KEY } from './services/mock-service'
import type { AppRuntime } from './services/runtime'
import { state } from './state'

export function startApp(root: HTMLElement, runtime: AppRuntime) {
  let page: Page | undefined, generation = 0
  let mountedHash: string | null = null
  let signedIn = runtime.signedIn
  runtime.onSessionExpired = () => { signedIn = false; state.paymentInputs.clear(); state.selectedIds.clear(); navigate('/login', true) }
  const login = async () => { await runtime.login(); signedIn = true; navigate('/home', true) }
  const logout = () => {
    signedIn = false; state.paymentInputs.clear(); state.selectedIds.clear()
    void runtime.logout().catch(() => { runtime.message = 'ログアウト処理を完了できませんでした。再度ログインしてください。' }).finally(() => navigate('/login', true))
    navigate('/login', true)
  }

  async function render(preserveViewport = false) {
    const keepViewport = preserveViewport && mountedHash === location.hash
    const scroll = { left: window.scrollX, top: window.scrollY }
    const focusKey = document.activeElement?.getAttribute('data-focus-key') || root.querySelector<HTMLElement>('[data-refresh-focus-key]')?.dataset.refreshFocusKey
    const current = ++generation
    page?.dispose?.(); page = undefined
    const route = parseRoute(location.hash)
    if (!signedIn && route.page !== 'login') { navigate('/login', true); return }
    if (signedIn && route.page === 'login') { navigate('/home', true); return }
    const service = runtime.service
    const shell = el('div', 'app-shell', appHeader(signedIn, logout), runtime.mode === 'demo' ? el('div', 'demo-banner', 'デモモード · 記録はこのブラウザーに保存') : null)
    const main = el('main'); shell.append(main)
    // Keep the existing page height while refreshing. A short loading placeholder
    // would make the browser clamp scrollY to zero before the new page is ready.
    if (!keepViewport) { root.replaceChildren(shell); main.append(el('p', 'loading', '読み込んでいます…')) }
    const context: PageContext = { service, isCurrent: () => current === generation, refresh: () => { void render(true) } }
    function mount(element: HTMLElement) {
      main.replaceChildren(element)
      if (keepViewport) root.replaceChildren(shell)
      mountedHash = location.hash
      const heading = main.querySelector('h1')
      document.title = `Roll & Pay${heading ? ` | ${heading.textContent}` : ''}`
      if (keepViewport) {
        const focusTarget = focusKey ? [...main.querySelectorAll<HTMLElement>('[data-focus-key]')].find(node => node.dataset.focusKey === focusKey && !node.matches(':disabled') && !node.hidden) : null
        // A rolled die becomes disabled. Focus the current result status instead,
        // without forcing keyboard users back to the page heading.
        const fallback = focusKey ? main.querySelector<HTMLElement>('.status') : null
        const activeTarget = focusTarget ?? fallback
        activeTarget?.focus({ preventScroll: true })
        window.scrollTo({ ...scroll, behavior: 'instant' })
      } else { window.scrollTo(0, 0); heading?.focus({ preventScroll: true }) }
    }
    try {
      let result: Page
      switch (route.page) {
        case 'login': result = loginPage(login, runtime.mode, runtime.message); break
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
      page = result; mount(result.element)
    } catch (reason) {
      if (!context.isCurrent()) return
      const errorPage = el('section', 'screen', title('PLEASE CHECK', '表示できませんでした。'), el('p', 'error', reason instanceof Error ? reason.message : '再度お試しください。'), button('再読み込み', () => { void render(true) }), link('ホームへ →', '/home'))
      const details = el('details', 'demo-options', el('summary', '', 'デモデータを初期化'))
      const action = actions(context, details)
      details.append(el('p', '', 'このブラウザーのデモ記録を破棄し、サンプル4件に戻します。'), button('確認してサンプルに戻す', () => { void action.run(async () => { await service.resetDemo(); navigate('/home', true) }) }, 'button danger'))
      if (runtime.mode === 'demo') errorPage.append(details)
      mount(errorPage)
    }
  }
  const refresh = () => { void render() }
  const onStorage = (event: StorageEvent) => { if (runtime.mode === 'demo' && (event.key === STORAGE_KEY || event.key === null)) void render(true) }
  window.addEventListener('hashchange', refresh); window.addEventListener('storage', onStorage)
  refresh()
  return () => { generation++; runtime.onSessionExpired = undefined; page?.dispose?.(); window.removeEventListener('hashchange', refresh); window.removeEventListener('storage', onStorage) }
}
