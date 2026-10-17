import type { Page } from '../domain/types'
import { actions, button, confirmDialog, el, link, title, yen } from '../components/ui'
import type { PageContext } from '../components/ui'
import { resumePath } from '../state'

export async function homePage(context: PageContext): Promise<Page> {
  const [totals, active] = await Promise.all([context.service.totals(), context.service.active()])
  const root = el('section', 'screen', title('GOOD FOOD, FAIR PLAY.', '今日も、おつかれさま。', '集まったみんなで、端数の運だめし。'))
  let dialog: HTMLDialogElement | undefined
  if (active) {
    root.append(el('div', 'notice', el('strong', '', '進行中のゲームがあります'), el('p', '', `${active.participants.map(p => p.name).join('・')} / ${active.participants.length}人`), link('ゲームを再開 →', resumePath(active), 'button primary'), button('このゲームを中止', () => {
      dialog = confirmDialog('ゲームを中止しますか？', '抽選途中の記録は負け額の履歴に追加されません。累積額も変わりません。', async () => { await context.service.cancel(active.id, active.revision); context.refresh() }, context)
    }, 'text-button')))
  } else root.append(link('⚄ 新しいゲーム →', '/participants', 'start-card'))
  root.append(el('div', 'section-heading', el('h2', '', '累積負け額'), el('span', '', 'これまでの合計')))
  const ranking = el('table', 'loss-ranking')
  ranking.setAttribute('aria-label', '累積負け額ランキング')
  const headings = ['順位', '名前', '金額'].map(label => {
    const heading = el('th', '', label)
    heading.scope = 'col'
    return heading
  })
  const rows = el('tbody')
  const sorted = [...totals].sort((a, b) => {
    if ((a.hasParticipated === false) !== (b.hasParticipated === false)) return a.hasParticipated === false ? 1 : -1
    return a.totalYen > b.totalYen ? -1 : a.totalYen < b.totalYen ? 1 : 0
  })
  let rank = 0
  sorted.forEach((total, index) => {
    const unplayed = total.hasParticipated === false
    if (!unplayed && (index === 0 || total.totalYen !== sorted[index - 1].totalYen)) rank = index + 1
    const position = el('td', 'loss-rank', unplayed ? '' : `${rank}位`)
    if (unplayed) position.setAttribute('aria-label', '順位なし')
    const amount = el('strong', '', unplayed ? '—' : yen(total.totalYen))
    if (unplayed) amount.setAttribute('aria-label', '未参加')
    rows.append(el('tr', '', position, el('td', 'loss-name', total.name, total.isActive === false ? '（現在は無効）' : ''), el('td', 'loss-amount', amount)))
  })
  ranking.append(el('thead', '', el('tr', '', ...headings)), rows)
  root.append(el('div', 'card totals', ranking), el('p', 'hint', 'ハイフンは参加実績なし'))
  if (context.service.mode === 'api') root.append(button('最新の状態を確認', () => context.refresh(), 'button secondary'))
  root.append(link('負け額の履歴を見る →', '/history'))
  const options = el('details', 'demo-options', el('summary', '', 'デモデータの管理'))
  const action = actions(context, options)
  const resetButton = button('初期化を確認', () => { resetConfirm.hidden = false; resetButton.hidden = true }, 'text-button')
  const resetConfirm = el('div', '', el('p', '', 'このブラウザーのデモ記録を破棄し、サンプル4件に戻します。'))
  resetConfirm.hidden = true
  resetConfirm.append(button('デモデータを初期化', () => { void action.run(async () => { await context.service.resetDemo(); context.refresh() }) }, 'button danger'), button('キャンセル', () => { resetConfirm.hidden = true; resetButton.hidden = false }, 'button secondary'))
  options.append(resetButton, resetConfirm)
  if (context.service.mode !== 'api') root.append(options)
  return { element: root, dispose: () => dialog?.remove() }
}
