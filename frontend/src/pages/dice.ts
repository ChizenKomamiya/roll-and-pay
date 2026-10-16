import { currentRound } from '../domain/game'
import type { Game, Page } from '../domain/types'
import { actions, button, el, link, title } from '../components/ui'
import type { PageContext } from '../components/ui'
import type { DiceView } from '../components/dice-view'
import { gamePath, navigate } from '../router'
import { resumePath } from '../state'

export async function dicePage(context: PageContext, id: string): Promise<Page> {
  const game = await context.service.get(id)
  if (!['rolling', 'tie', 'awaitingPayment'].includes(game.status)) { navigate(game.status === 'completed' ? resumePath(game) : '/home', true); return { element: el('section') } }
  const round = currentRound(game), remaining = game.participants.filter(p => round.rolls[p.id] === null)
  const loser = game.participants.find(p => p.id === game.loserId)
  const heading = game.status === 'tie' ? '同点！ もう一度。' : loser ? `今日の端数は、${loser.name}。` : remaining.length === game.participants.length ? 'さあ、運だめし。' : `あと${remaining.length}人、どうぞ。`
  const root = el('section', 'screen', title(`STEP 02 / ROUND ${round.number}`, heading, '最も小さい出目の人が端数を負担します。'))
  const status = el('div', 'status'); status.setAttribute('role', 'status'); status.setAttribute('aria-live', 'polite')
  status.tabIndex = -1
  status.append(el('strong', '', game.status === 'tie' ? '最小の出目が同点' : loser ? '敗者確定' : remaining.length === game.participants.length ? 'まだ振っていません' : '他の人の操作待ち'), el('span', '', `${game.participants.length - remaining.length} / ${game.participants.length}人`))
  root.append(status)
  const grid = el('div', 'dice-grid'), views = new Map<string, DiceView>()
  const action = actions(context, root)
  const { diceView } = await import('../components/dice-view')
  if (!context.isCurrent()) return { element: root }
  async function roll(participantId?: string) {
    await action.run(async () => {
      const previousStatus = status.textContent ?? ''
      status.replaceChildren(el('strong', '', '抽選中 · 操作をお待ちください'))
      try {
        const updated: Game = await context.service.roll(id, game.revision, participantId)
        const next = currentRound(updated)
        if (next.number === round.number) await Promise.all([...views].filter(([pid]) => round.rolls[pid] === null && next.rolls[pid] !== null).map(([pid, view]) => view.play(next.rolls[pid]!)))
        if (context.isCurrent()) context.refresh()
      } catch (error) {
        status.textContent = previousStatus
        throw error
      }
    })
  }
  game.participants.forEach(participant => {
    const value = round.rolls[participant.id], view = diceView(value)
    views.set(participant.id, view)
    const card = button('', () => { void roll(participant.id) }, `dice-card${loser?.id === participant.id ? ' loser' : ''}`)
    card.dataset.focusKey = `die-${participant.id}`
    card.disabled = value !== null || game.status !== 'rolling' || context.service.capabilities?.roll === false
    card.setAttribute('aria-label', value === null ? `${participant.name}のサイコロを振る` : `${participant.name} 出目${value}${loser?.id === participant.id ? ' 敗者' : ''}`)
    card.append(el('span', '', participant.name, loser?.id === participant.id ? el('em', '', '敗者') : null), view.element, el('small', '', value === null ? 'タップして振る' : `出目 ${value} · 確定`))
    grid.append(card)
  })
  root.append(grid)
  if (context.service.capabilities?.roll === false) root.append(el('p', 'notice', 'サイコロの抽選・振り直しは準備中です。保存済みの出目を表示しています。'), button('最新の状態を確認', () => context.refresh(), 'button secondary'))
  if (game.status === 'rolling' && context.service.capabilities?.roll !== false) root.append(button(remaining.length === game.participants.length ? '全員まとめて振る' : `残り${remaining.length}人をまとめて振る`, () => { void roll() }), el('p', 'hint', '確定した出目は変わりません。各自の操作もできます。'))
  if (game.status === 'tie' && context.service.capabilities?.roll !== false) root.append(el('div', 'notice', el('strong', '', '参加者全員で振り直します'), el('p', '', '前の出目にかかわらず、全員が敗者の候補です。')), button('全員で振り直す →', () => { void action.run(async () => { await context.service.nextRound(id, game.revision); if (context.isCurrent()) context.refresh() }) }))
  if (loser) root.append(link('会計総額を入力 →', gamePath(id, 'payment'), 'button primary'), el('p', 'hint', '敗者確定後は振り直せません。'))
  if (context.service.capabilities?.roll === true) root.append(button('最新の状態を確認', () => context.refresh(), 'button secondary'))
  root.append(link('ホームへ戻る（ゲームは保持）', '/home', 'back lower-back'))
  return { element: root, dispose: () => views.forEach(view => view.dispose()) }
}
