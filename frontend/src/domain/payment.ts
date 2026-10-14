import type { Participant, Payment } from './types'

export const MAX_TOTAL_YEN = 9_999_999

export function parseTotalYen(text: string): number {
  const normalized = text.trim().replace(/[０-９]/g, c => String.fromCharCode(c.charCodeAt(0) - 0xfee0))
  if (!/^(?:[0-9]+|[1-9][0-9]{0,2}(?:,[0-9]{3})+)$/.test(normalized)) {
    throw new Error('会計総額は整数円で入力してください。')
  }
  const value = Number(normalized.replaceAll(',', ''))
  if (!Number.isSafeInteger(value) || value < 1 || value > MAX_TOTAL_YEN) {
    throw new Error('会計総額は1〜9,999,999円で入力してください。')
  }
  return value
}

export function calculatePayment(totalYen: number, participants: Participant[], loserId: string): Payment {
  if (!Number.isSafeInteger(totalYen) || totalYen < 1 || totalYen > MAX_TOTAL_YEN) throw new Error('会計総額が範囲外です。')
  if (participants.length < 2 || participants.length > 6 || new Set(participants.map(p => p.id)).size !== participants.length) throw new Error('参加者は重複なしで2〜6人にしてください。')
  if (!participants.some(p => p.id === loserId)) throw new Error('敗者が参加者に含まれていません。')
  const baseYen = Math.floor(totalYen / (participants.length * 1000)) * 1000
  const lossYen = totalYen - baseYen * participants.length
  return {
    totalYen, baseYen, lossYen, loserPaymentYen: baseYen + lossYen,
    collectFromOthersYen: baseYen * (participants.length - 1),
    shares: participants.map(p => ({ participantId: p.id, amountYen: baseYen + (p.id === loserId ? lossYen : 0) })),
  }
}
