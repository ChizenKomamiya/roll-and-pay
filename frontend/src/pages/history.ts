import type { Game, Page } from '../domain/types'
import { dateLabel, el, link, title, yen } from '../components/ui'
import type { PageContext } from '../components/ui'
import { gamePath } from '../router'

export function historyCard(game: Game) {
  const loser = game.participants.find(p => p.id === game.loserId)!
  const card = link('', gamePath(game.id, 'detail'), 'history-card')
  card.append(el('div', '', el('span', 'muted', dateLabel(game.createdAt)), el('strong', '', `${loser.name}の負け額`), el('span', '', `会計 ${yen(game.payment!.totalYen)} · ${game.participants.length}人`)), el('div', 'history-right', el('strong', '', yen(game.payment!.lossYen)), el('span', '', '詳細 →')))
  return card
}

export async function historyPage(context: PageContext): Promise<Page> {
  const games = await context.service.history()
  const root = el('section', 'screen', link('← ホーム', '/home', 'back'), title('LOSS HISTORY', '誰が、いくら負けた？', '一回ごとの負け額を振り返ります。'), el('div', 'section-heading', el('h2', '', 'ゲーム履歴'), el('span', '', `新しい順 · ${games.length}件`)))
  if (games.length) root.append(...games.map(historyCard), el('p', 'hint', '表示額は累積に加算する負け額です。'))
  else root.append(el('div', 'card empty', el('h2', '', 'まだ履歴がありません'), el('p', 'muted', 'ゲームの結果を保存すると、ここに表示されます。')), link('新しいゲームを始める →', '/participants'))
  return { element: root }
}
