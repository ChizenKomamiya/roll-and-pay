import type { Page } from '../domain/types'
import { paymentSummary } from '../components/payment-summary'
import { actions, button, dateLabel, el, link, title, yen } from '../components/ui'
import type { PageContext } from '../components/ui'
import { gamePath, navigate } from '../router'

export async function historyDetailPage(context: PageContext, id: string, deleting = false): Promise<Page> {
  const game = await (context.service.historyDetail?.(id) ?? context.service.get(id))
  if (game.status !== 'completed' || !game.payment) throw new Error('保存済みの履歴が見つかりません。')
  const loser = game.participants.find(p => p.id === game.loserId)!
  const root = el('section', 'screen')
  if (deleting) {
    root.append(link('← 履歴詳細', gamePath(id, 'detail'), 'back'), title('DELETE HISTORY', 'この履歴を削除しますか？'), el('div', 'card', el('p', 'muted', `${dateLabel(game.createdAt)} / ${game.participants.length}人`), el('div', 'pay-row', el('span', '', `${loser.name}の負け額`), el('strong', '', yen(game.payment.lossYen))), el('p', '', `会計総額：${yen(game.payment.totalYen)}`)), el('div', 'notice', el('strong', '', '累積負け額からも除外します'), el('p', '', `${loser.name}の累積負け額から${yen(game.payment.lossYen)}を除外します。この操作は元に戻せません。`)))
    const action = actions(context, root)
    root.append(button('削除する', () => { void action.run(async () => {
      await context.service.delete(id, game.revision)
      if (!context.isCurrent()) return
      navigate('/history/deleted', true)
    }) }, 'button danger'), link('キャンセル', gamePath(id, 'detail')))
  } else {
    root.append(link('← 負け額の履歴', '/history', 'back'), title('LOSS HISTORY / DETAIL', `${loser.name}の負け額の記録`, `${dateLabel(game.createdAt)} / ${game.participants.length}人`), paymentSummary(game, game.payment), el('p', 'hint', `千円単位の基準負担額：${yen(game.payment.baseYen)} / 金額登録：${dateLabel(game.completedAt!)}`))
    root.append(link('この履歴を削除', gamePath(id, 'delete'), 'button danger'))
  }
  if (context.service.mode === 'api') root.append(button('最新の状態を確認', () => context.refresh(), 'button secondary'))
  return { element: root }
}
