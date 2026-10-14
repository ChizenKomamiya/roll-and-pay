export interface Participant { id: string; name: string }
export type GameStatus = 'rolling' | 'tie' | 'awaitingPayment' | 'completed' | 'cancelled' | 'deleted'
export interface Round { number: number; rolls: Record<string, number | null> }
export interface Payment {
  totalYen: number
  baseYen: number
  lossYen: number
  loserPaymentYen: number
  collectFromOthersYen: number
  shares: { participantId: string; amountYen: number }[]
}
export interface Game {
  id: string
  revision: number
  status: GameStatus
  createdAt: string
  completedAt: string | null
  participants: Participant[]
  rounds: Round[]
  loserId: string | null
  payment: Payment | null
}
export interface LossTotal extends Participant { totalYen: bigint; hasParticipated: boolean }
export interface StoreDocument { version: 1; games: Game[] }
export interface Page { element: HTMLElement; dispose?: () => void }
