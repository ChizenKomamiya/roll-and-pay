import type { Game, LossTotal, Participant, Payment } from '../domain/types'

/** Pages depend on this contract. The demo implementation owns random results and persistence. */
export interface GameService {
  participants(): Promise<Participant[]>
  totals(): Promise<LossTotal[]>
  active(): Promise<Game | null>
  history(): Promise<Game[]>
  get(id: string): Promise<Game>
  create(ids: string[]): Promise<Game>
  roll(id: string, revision: number, participantId?: string): Promise<Game>
  nextRound(id: string, revision: number): Promise<Game>
  preview(id: string, totalYen: number): Promise<Payment>
  save(id: string, revision: number, totalYen: number): Promise<Game>
  cancel(id: string, revision: number): Promise<void>
  delete(id: string, revision: number): Promise<void>
  resetDemo(): Promise<void>
}
