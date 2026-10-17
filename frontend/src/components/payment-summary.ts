import type { Game, Payment } from '../domain/types'
import { currentRound } from '../domain/game'
import { el, yen } from './ui'

export function paymentSummary(game: Game, payment: Payment, options: { showRolls?: boolean; showLossHint?: boolean } = {}) {
  const loser = game.participants.find(p => p.id === game.loserId)!
  const card = el('div', 'result-card',
    el('div', 'result-top', el('span', '', `会計総額 · ${game.participants.length}人`), el('strong', '', yen(payment.totalYen))),
    el('div', 'loss-highlight', el('span', '', `${loser.name}の負け額`), el('strong', '', yen(payment.lossYen)), options.showLossHint === false ? null : el('small', '', '累積負け額に加算する金額')),
  )
  game.participants.forEach(participant => {
    const label = el('span', '', participant.name)
    if (options.showRolls) label.append(el('small', '', `出目${currentRound(game).rolls[participant.id]}`))
    if (participant.id === loser.id) label.append(el('small', '', '敗者'))
    const amount = payment.shares.find(share => share.participantId === participant.id)!.amountYen
    card.append(el('div', `pay-row${participant.id === loser.id ? ' accent' : ''}`, label, el('strong', '', yen(amount))))
  })
  card.append(el('p', 'settlement', `${loser.name}以外の${game.participants.length - 1}人は、${loser.name}に各${yen(payment.baseYen)}を渡します。${loser.name}は${yen(payment.collectFromOthersYen)}を受け取り、会計${yen(payment.totalYen)}をまとめて支払います。`))
  return card
}
