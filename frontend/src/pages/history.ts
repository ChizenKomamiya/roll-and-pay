import type { Game, HistoryItem, Page } from '../domain/types'
import { actions, button, dateLabel, el, link, title, yen } from '../components/ui'
import type { PageContext } from '../components/ui'
import { gamePath } from '../router'

export function historyCard(game: Game) {
  const loser = game.participants.find(p => p.id === game.loserId)!
  return summaryCard({ id: game.id, createdAt: game.createdAt, loser, participantCount: game.participants.length, totalYen: game.payment!.totalYen, lossYen: game.payment!.lossYen })
}
function summaryCard(item: HistoryItem) {
  const card = link('', gamePath(item.id, 'detail'), 'history-card')
  card.append(el('div', '', el('span', 'muted', dateLabel(item.createdAt)), el('strong', '', item.loser.name), el('span', '', `会計 ${yen(item.totalYen)} · ${item.participantCount}人`)), el('div', 'history-right', el('strong', '', yen(item.lossYen)), el('span', '', '詳細 →')))
  return card
}

export async function historyPage(context: PageContext): Promise<Page> {
  if (context.service.historyPage) {
    const first = await context.service.historyPage()
    let cursor = first.nextCursor, disposed = false
    const ids = new Set(first.items.map(item => item.id)), cursors = new Set<string>()
    const count = el('span', '', `新しい順 · ${ids.size}件表示`)
    const list = el('div', '', ...first.items.map(summaryCard))
    const root = el('section', 'screen', link('← ホーム', '/home', 'back'), title('LOSS HISTORY', '誰が、いくら負けた？', '一回ごとの負け額を振り返ります。'), el('div', 'section-heading', el('h2', '', 'ゲーム履歴'), count), list)
    const action = actions(context, root)
    const more = button('さらに表示', () => { void action.run(async () => {
      if (!cursor) return
      const requested = cursor
      const next = await context.service.historyPage!(requested)
      if (disposed || !context.isCurrent()) return
      if (next.nextCursor && (next.nextCursor === requested || cursors.has(next.nextCursor))) throw new Error('続きの履歴を取得できませんでした。再読み込みしてください。')
      cursors.add(requested)
      for (const item of next.items) { if (!ids.has(item.id)) { ids.add(item.id); list.append(summaryCard(item)) } }
      cursor = next.nextCursor; more.hidden = cursor === null; count.textContent = `新しい順 · ${ids.size}件表示`
    }) }, 'button secondary')
    more.hidden = cursor === null; root.append(more)
    if (!first.items.length && !cursor) root.append(el('div', 'card empty', el('h2', '', 'まだ履歴がありません'), el('p', 'muted', 'ゲームの結果を保存すると、ここに表示されます。')), link('新しいゲームを始める →', '/participants'))
    else root.append(el('p', 'hint', '表示額は累積に加算する負け額です。'))
    return { element: root, dispose: () => { disposed = true } }
  }
  const games = await context.service.history()
  const root = el('section', 'screen', link('← ホーム', '/home', 'back'), title('LOSS HISTORY', '誰が、いくら負けた？', '一回ごとの負け額を振り返ります。'), el('div', 'section-heading', el('h2', '', 'ゲーム履歴'), el('span', '', `新しい順 · ${games.length}件`)))
  if (games.length) root.append(...games.map(historyCard), el('p', 'hint', '表示額は累積に加算する負け額です。'))
  else root.append(el('div', 'card empty', el('h2', '', 'まだ履歴がありません'), el('p', 'muted', 'ゲームの結果を保存すると、ここに表示されます。')), link('新しいゲームを始める →', '/participants'))
  return { element: root }
}
