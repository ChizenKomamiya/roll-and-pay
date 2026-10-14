import type { Game, Round } from './types'

export function currentRound(game: Game): Round {
  const round = game.rounds.at(-1)
  if (!round) throw new Error('抽選情報が見つかりません。')
  return round
}

export function judgeRolls(rolls: Record<string, number | null>): { status: 'rolling' | 'tie' | 'awaitingPayment'; loserId: string | null } {
  const entries = Object.entries(rolls)
  if (!entries.length) throw new Error('参加者がいません。')
  if (entries.some(([, v]) => v !== null && (!Number.isInteger(v) || v < 1 || v > 6))) throw new Error('出目が不正です。')
  if (entries.some(([, v]) => v === null)) return { status: 'rolling', loserId: null }
  const minimum = Math.min(...entries.map(([, v]) => v!))
  const losers = entries.filter(([, v]) => v === minimum)
  return losers.length === 1 ? { status: 'awaitingPayment', loserId: losers[0][0] } : { status: 'tie', loserId: null }
}
