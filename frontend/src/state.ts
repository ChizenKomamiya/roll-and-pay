import type { Game } from './domain/types'
import { gamePath } from './router'

export const state = { selectedIds: new Set<string>(), paymentInputs: new Map<string, string>() }
export function resumePath(game: Game): string {
  return gamePath(game.id, game.status === 'completed' ? 'result' : game.status === 'awaitingPayment' ? 'payment' : 'dice')
}
