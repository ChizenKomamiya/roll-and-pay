import type { GameService } from './game-service'
import { ApiError } from './api-client'
import type { ApiClient } from './api-client'
import { mapGame, mapHistory, mapParticipants, mapPayment, object } from './api-models'
import { parseTotalYen } from '../domain/payment'
import { currentRound } from '../domain/game'
import type { Game } from '../domain/types'

type StoragePort = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>
export function createApiService(client: ApiClient, storage: StoragePort, pendingKey: string): GameService {
  const unavailable = async (): Promise<never> => { throw new Error('この機能は現在準備中です。') }
  const games = new Map<string, Game>(), updating = new Set<string>()
  type DiceCommand = { key: string; revision: number; path: string; body: Record<string, unknown>; ifMatch: string }
  const commandKey = (id: string) => `${pendingKey}:dice:${id}`
  function pendingDice(id: string): DiceCommand | null {
    const raw = storage.getItem(commandKey(id))
    if (!raw) return null
    const command = object(JSON.parse(raw))
    if (typeof command.key !== 'string' || !Number.isSafeInteger(command.revision) || typeof command.path !== 'string' || typeof command.ifMatch !== 'string') throw new Error('送信待ちの抽選要求を読み込めません。')
    object(command.body)
    return command as DiceCommand
  }
  const get = async (id: string) => {
    const response = await client.request(`games/${encodeURIComponent(id)}`)
    const game = mapGame(response.data, response.etag)
    if (game.id !== id) throw new Error('ゲームの応答を確認できませんでした。')
    games.set(id, game)
    const pending = pendingDice(id)
    // A newer revision makes the old If-Match unable to apply again, even after a lost response.
    if (pending && game.revision > pending.revision) storage.removeItem(commandKey(id))
    return game
  }
  async function dice(id: string, revision: number, nextRound: boolean, participantId?: string): Promise<Game> {
    if (updating.has(id)) throw new Error('抽選の操作中です。しばらくお待ちください。')
    updating.add(id)
    try {
      const game = games.get(id) ?? await get(id)
      if (game.revision !== revision) throw new Error('記録が更新されました。最新の状態を確認してください。')
      const roundNumber = currentRound(game).number
      const path = `games/${encodeURIComponent(id)}/${nextRound ? 'rounds' : 'rolls'}`
      const body = nextRound ? { previousRoundNumber: roundNumber } : participantId === undefined ? { roundNumber, mode: 'remaining' } : { roundNumber, mode: 'individual', participantId }
      const ifMatch = `"g-${game.id}-v${game.revision}"`
      let command = pendingDice(id)
      if (command && (command.path !== path || command.ifMatch !== ifMatch || JSON.stringify(command.body) !== JSON.stringify(body))) throw new Error('直前の抽選結果が未確認です。同じ操作を再送するか、最新の状態を確認してください。')
      if (!command) { command = { key: crypto.randomUUID(), revision, path, body, ifMatch }; storage.setItem(commandKey(id), JSON.stringify(command)) }
      try {
        const response = await client.request(path, { method: 'POST', body: command.body, ifMatch: command.ifMatch, idempotencyKey: command.key })
        const result = mapGame(response.data, response.etag)
        if (result.id !== id) throw new Error('ゲームの応答を確認できませんでした。')
        // Replayed responses can be old; render only the latest authoritative game.
        const latest = await get(id)
        if (latest.revision < result.revision) throw new Error('最新のゲームを確認できませんでした。もう一度状態を確認してください。')
        storage.removeItem(commandKey(id))
        return latest
      } catch (error) {
        if (error instanceof ApiError && [400, 403, 404, 409, 410, 412, 422, 428].includes(error.status)) {
          storage.removeItem(commandKey(id))
          if (error.status === 409 || error.status === 412) { try { await get(id) } catch { /* Keep the original conflict message. */ } }
        }
        throw error
      }
    } finally { updating.delete(id) }
  }
  let creating = false
  return {
    capabilities: { totals: false, history: true, roll: true, cancel: false, save: false, delete: false },
    async participants() { return mapParticipants((await client.request('participants')).data) },
    totals: unavailable,
    async active() {
      const response = await client.request('games/active'), game = object(response.data).game
      return game === null ? null : mapGame(game, response.etag)
    },
    get,
    pendingParticipants() {
      const raw = storage.getItem(pendingKey)
      if (!raw) return null
      const pending = object(JSON.parse(raw))
      if (!Array.isArray(pending.ids) || pending.ids.some(id => typeof id !== 'string')) throw new Error('送信待ちの記録を読み込めません。')
      return pending.ids as string[]
    },
    history: unavailable,
    async historyPage(cursor) {
      const query = new URLSearchParams({ limit: '20' })
      if (cursor !== undefined) query.set('cursor', cursor)
      return mapHistory((await client.request(`games?${query}`)).data)
    },
    async historyDetail(id) {
      const response = await client.request(`games/${encodeURIComponent(id)}/history-detail`)
      return mapGame(response.data, response.etag, true)
    },
    async create(ids) {
      if (creating) throw new Error('ゲームを作成中です。しばらくお待ちください。')
      if (ids.length < 2 || ids.length > 6 || new Set(ids).size !== ids.length) throw new Error('参加者を2〜6人選択してください。')
      creating = true
      try {
        // Persist BEFORE sending so a response loss or reload keeps the original command.
        let pending: { key: string; ids: string[] }
        const raw = storage.getItem(pendingKey)
        if (raw) {
          pending = JSON.parse(raw)
          if (typeof pending.key !== 'string' || !Array.isArray(pending.ids)) throw new Error('送信待ちの記録を読み込めません。ストレージ設定を確認してください。')
          if (JSON.stringify(pending.ids) !== JSON.stringify(ids)) throw new Error('作成結果が未確認です。直前と同じ参加者・順序で再送するか、ホームで進行中のゲームを確認してください。')
        } else { pending = { key: crypto.randomUUID(), ids: [...ids] }; storage.setItem(pendingKey, JSON.stringify(pending)) }
        try {
          const response = await client.request('games', { method: 'POST', body: { participantIds: pending.ids }, idempotencyKey: pending.key })
          const game = mapGame(response.data, response.etag)
          // Replay may describe an old revision. Always read the current game.
          const latest = await get(game.id)
          storage.removeItem(pendingKey)
          return latest
        } catch (error) {
          if (error instanceof ApiError && [400, 403, 409, 410, 422].includes(error.status)) storage.removeItem(pendingKey)
          throw error
        }
      } finally { creating = false }
    },
    async preview(id, totalYen) {
      parseTotalYen(String(totalYen))
      const game = await get(id)
      const response = await client.request(`games/${encodeURIComponent(id)}/payment-preview`, { method: 'POST', body: { totalYen } })
      const result = object(response.data)
      if (result.gameId !== id || result.revision !== game.revision || response.etag !== `"g-${id}-v${game.revision}"` || !game.loserId) throw new Error('記録が更新されました。再読み込みして負担額を確認してください。')
      const payment = mapPayment(result.payment, game.participants, game.loserId)
      if (payment.totalYen !== totalYen) throw new Error('プレビューの金額を確認できませんでした。再度お試しください。')
      return payment
    },
    roll: (id, revision, participantId) => dice(id, revision, false, participantId),
    nextRound: (id, revision) => dice(id, revision, true),
    save: unavailable, cancel: unavailable, delete: unavailable, resetDemo: unavailable,
  }
}
