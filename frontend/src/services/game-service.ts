import type { Game, HistoryPage, LossTotal, Participant, Payment } from '../domain/types'

/** Pages depend on this contract; API results are authoritative in API mode. */
export interface GameService {
  mode?: 'api' | 'demo'
  participants(): Promise<Participant[]>
  totals(): Promise<LossTotal[]>
  active(): Promise<Game | null>
  history(): Promise<Game[]>
  historyPage?(cursor?: string): Promise<HistoryPage>
  historyDetail?(id: string): Promise<Game>
  pendingParticipants?(): string[] | null
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
