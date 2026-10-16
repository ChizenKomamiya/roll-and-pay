import type { Page } from '../domain/types'
import { actions, button, confirmDialog, el, link, title, yen } from '../components/ui'
import type { PageContext } from '../components/ui'
import { resumePath } from '../state'

export async function homePage(context: PageContext): Promise<Page> {
  const [totals, active] = await Promise.all([context.service.capabilities?.totals === false ? null : context.service.totals(), context.service.active()])
  const root = el('section', 'screen', title('GOOD FOOD, FAIR PLAY.', '今日も、おつかれさま。', '集まったみんなで、端数の運だめし。'))
  let dialog: HTMLDialogElement | undefined
  if (active) {
    root.append(el('div', 'notice', el('strong', '', '進行中のゲームがあります'), el('p', '', `${active.participants.map(p => p.name).join('・')} / ${active.participants.length}人`), link('ゲームを再開 →', resumePath(active), 'button primary'), context.service.capabilities?.cancel === false ? null : button('このゲームを中止', () => {
      dialog = confirmDialog('ゲームを中止しますか？', '抽選途中の記録は負け額の履歴に追加されません。累積額も変わりません。', async () => { await context.service.cancel(active.id, active.revision); context.refresh() }, context)
    }, 'text-button')))
    if (context.service.capabilities?.cancel === false) {
      root.append(el('p', 'hint', 'ゲームの中止は準備中です。進行中のゲームは再開して確認できます。'))
    }
  } else root.append(link('⚄ 新しいゲーム →', '/participants', 'start-card'))
  root.append(el('div', 'section-heading', el('h2', '', '累積負け額'), el('span', '', 'これまでの合計')))
  const grid = el('div', 'card totals')
  totals?.forEach(total => { const amount = el('strong', '', total.hasParticipated ? yen(total.totalYen) : '—'); if (!total.hasParticipated) amount.setAttribute('aria-label', '未参加'); grid.append(el('div', '', el('span', '', total.name), amount)) })
  if (totals) root.append(grid, el('p', 'hint', '— は未参加、0円は参加済みで負け額が0円です。'))
  else root.append(el('p', 'hint', '累積負け額は準備中です。保存済みの負け額は履歴で確認できます。'))
  root.append(link('負け額の履歴を見る →', '/history'))
  const options = el('details', 'demo-options', el('summary', '', 'デモデータの管理'))
  const action = actions(context, options)
  const resetButton = button('初期化を確認', () => { resetConfirm.hidden = false; resetButton.hidden = true }, 'text-button')
  const resetConfirm = el('div', '', el('p', '', 'このブラウザーのデモ記録を破棄し、サンプル4件に戻します。'))
  resetConfirm.hidden = true
  resetConfirm.append(button('デモデータを初期化', () => { void action.run(async () => { await context.service.resetDemo(); context.refresh() }) }, 'button danger'), button('キャンセル', () => { resetConfirm.hidden = true; resetButton.hidden = false }, 'button secondary'))
  options.append(resetButton, resetConfirm)
  if (!context.service.capabilities) root.append(options)
  return { element: root, dispose: () => dialog?.remove() }
}
