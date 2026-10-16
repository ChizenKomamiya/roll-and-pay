import type { GameService } from './game-service'
import { ApiError } from './api-client'
import type { ApiClient } from './api-client'
import { mapGame, mapHistory, mapParticipants, mapPayment, object } from './api-models'
import { parseTotalYen } from '../domain/payment'

type StoragePort = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>
export function createApiService(client: ApiClient, storage: StoragePort, pendingKey: string): GameService {
  const unavailable = async (): Promise<never> => { throw new Error('この機能は現在準備中です。') }
  const get = async (id: string) => { const response = await client.request(`games/${encodeURIComponent(id)}`); return mapGame(response.data, response.etag) }
  let creating = false
  return {
    capabilities: { totals: false, history: true, roll: false, cancel: false, save: false, delete: false },
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
    roll: unavailable, nextRound: unavailable, save: unavailable, cancel: unavailable, delete: unavailable, resetDemo: unavailable,
  }
}
