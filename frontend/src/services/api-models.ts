import type { Game, HistoryPage, Participant, Payment } from '../domain/types'

function invalid(): never { throw new Error('API応答の形式を確認できませんでした。再読み込みしてください。') }
export function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return invalid()
  return value as Record<string, unknown>
}
function array(value: unknown): unknown[] { return Array.isArray(value) ? value : invalid() }
function text(value: unknown): string { return typeof value === 'string' && value.length > 0 ? value : invalid() }
function integer(value: unknown, min = 0, max = Number.MAX_SAFE_INTEGER): number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= min && value <= max ? value : invalid()
}
function timestamp(value: unknown): string { const result = text(value); return Number.isFinite(Date.parse(result)) ? result : invalid() }
function participant(value: unknown): Participant { const p = object(value); return { id: text(p.participantId), name: text(p.displayName) } }
export function mapParticipants(value: unknown): Participant[] {
  const people = array(object(value).participants).map(participant)
  if (new Set(people.map(p => p.id)).size !== people.length) return invalid()
  return people
}
export function mapPayment(value: unknown, participants: Participant[], loserId: string): Payment {
  const p = object(value)
  if (p.loserParticipantId !== loserId || p.participantCount !== participants.length || p.calculationVersion !== 'thousand-remainder-v1') return invalid()
  const shares = array(p.shares).map(value => { const share = object(value); return { participantId: text(share.participantId), amountYen: integer(share.amountYen) } })
  if (shares.length !== participants.length || new Set(shares.map(s => s.participantId)).size !== participants.length || shares.some(s => !participants.some(p => p.id === s.participantId))) return invalid()
  const payment = { totalYen: integer(p.totalYen, 1, 9_999_999), baseYen: integer(p.baseYen), lossYen: integer(p.lossYen), loserPaymentYen: integer(p.loserPaymentYen), collectFromOthersYen: integer(p.collectFromOthersYen), shares }
  if (shares.reduce((sum, share) => sum + share.amountYen, 0) !== payment.totalYen || shares.find(s => s.participantId === loserId)?.amountYen !== payment.loserPaymentYen || payment.collectFromOthersYen + payment.loserPaymentYen !== payment.totalYen) return invalid()
  return payment
}
export function mapGame(value: unknown, etag: string | null, history = false): Game {
  const g = object(value), id = text(g.gameId), revision = integer(g.revision, 1)
  if (!/^[\da-f]{8}-(?:[\da-f]{4}-){3}[\da-f]{12}$/i.test(id) || etag !== `"g-${id}-v${revision}"`) return invalid()
  const status = history ? 'completed' : text(g.status)
  if (!['rolling', 'tie', 'awaitingPayment', 'completed', 'cancelled'].includes(status)) return invalid()
  const participants = mapParticipants(g)
  if (participants.length < 2 || participants.length > 6) return invalid()
  const loserId = g.loserParticipantId === null ? null : text(g.loserParticipantId)
  if (loserId && !participants.some(p => p.id === loserId)) return invalid()
  const payment = g.payment === null ? null : mapPayment(g.payment, participants, loserId ?? invalid())
  if ((status === 'completed' && !payment) || (['completed', 'awaitingPayment'].includes(status) && !loserId)) return invalid()
  const rounds: Game['rounds'] = []
  if (!history) {
    const r = object(g.currentRound)
    const rolls = array(r.rolls).map(value => { const roll = object(value); return [text(roll.participantId), roll.value === null ? null : integer(roll.value, 1, 6)] as const })
    if (rolls.length !== participants.length || new Set(rolls.map(([id]) => id)).size !== participants.length || rolls.some(([id]) => !participants.some(p => p.id === id))) return invalid()
    rounds.push({ number: integer(r.roundNumber, 1), rolls: Object.fromEntries(rolls) })
  }
  return { id, revision, status: status as Game['status'], createdAt: timestamp(g.createdAt), completedAt: g.completedAt === null ? null : timestamp(g.completedAt), participants, loserId, payment, rounds }
}
export function mapHistory(value: unknown): HistoryPage {
  const page = object(value)
  return { items: array(page.items).map(value => {
    const item = object(value)
    return { id: text(item.gameId), createdAt: timestamp(item.createdAt), loser: participant(item.loser), participantCount: integer(item.participantCount, 2, 6), totalYen: integer(item.totalYen, 1, 9_999_999), lossYen: integer(item.lossYen) }
  }), nextCursor: page.nextCursor === null ? null : text(page.nextCursor) }
}
