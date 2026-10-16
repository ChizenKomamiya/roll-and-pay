import type { GameService } from './game-service'
import { ApiError } from './api-client'
import type { ApiClient } from './api-client'
import { mapGame, mapHistory, mapParticipants, mapPayment, mapLossTotals, mapDeleted, object } from './api-models'
import { parseTotalYen } from '../domain/payment'
import { currentRound } from '../domain/game'
import type { Game } from '../domain/types'

type StoragePort = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>
export function createApiService(client: ApiClient, storage: StoragePort, pendingKey: string): GameService {
  const previews = new Map<string, { revision: number; totalYen: number; etag: string }>()
  const totals = async () => mapLossTotals((await client.request('statistics/losses')).data)
  const games = new Map<string, Game>(), updating = new Set<string>()
  type GameCommand = { key: string; revision: number; path: string; body?: Record<string, unknown>; ifMatch: string; method?: 'POST' | 'DELETE' }
  const commandKey = (id: string) => `${pendingKey}:dice:${id}`
  function pendingCommand(id: string): GameCommand | null {
    const raw = storage.getItem(commandKey(id))
    if (!raw) return null
    const command = object(JSON.parse(raw))
    if (typeof command.key !== 'string' || !Number.isSafeInteger(command.revision) || typeof command.path !== 'string' || typeof command.ifMatch !== 'string') throw new Error('送信待ちの更新要求を読み込めません。')
    if (command.method !== undefined && command.method !== 'POST' && command.method !== 'DELETE') throw new Error('送信待ちの更新要求を読み込めません。')
    if (command.body !== undefined) object(command.body)
    return command as GameCommand
  }
  const get = async (id: string) => {
    let response
    try { response = await client.request(`games/${encodeURIComponent(id)}`) }
    catch (error) {
      if (error instanceof ApiError && [404, 410].includes(error.status)) {
        games.delete(id); previews.delete(id); storage.removeItem(commandKey(id))
      }
      throw error
    }
    const game = mapGame(response.data, response.etag)
    if (game.id !== id) throw new Error('ゲームの応答を確認できませんでした。')
    games.set(id, game)
    const pending = pendingCommand(id)
    // A newer revision makes the old If-Match unable to apply again, even after a lost response.
    if (pending && game.revision > pending.revision) storage.removeItem(commandKey(id))
    return game
  }
  // Keep the existing storage suffix so in-flight dice commands survive an upgrade.
  async function mutate(id: string, revision: number, suffix: string, body?: Record<string, unknown>, method: 'POST' | 'DELETE' = 'POST'): Promise<Game | null> {
    if (updating.has(id)) throw new Error('更新の操作中です。しばらくお待ちください。')
    updating.add(id)
    try {
      const game = games.get(id) ?? await get(id)
      if (game.revision !== revision) throw new Error('記録が更新されました。最新の状態を確認してください。')
      const path = `games/${encodeURIComponent(id)}${suffix}`
      const ifMatch = `"g-${game.id}-v${game.revision}"`
      let command = pendingCommand(id)
      if (command && (command.path !== path || command.ifMatch !== ifMatch || (command.method ?? 'POST') !== method || JSON.stringify(command.body) !== JSON.stringify(body))) throw new Error('直前の操作結果が未確認です。同じ操作を再送するか、最新の状態を確認してください。')
      if (!command) { command = { key: crypto.randomUUID(), revision, path, body, ifMatch, method }; storage.setItem(commandKey(id), JSON.stringify(command)) }
      try {
        const response = await client.request(path, { method, body: command.body, ifMatch: command.ifMatch, idempotencyKey: command.key })
        if (method === 'DELETE') {
          mapDeleted(response.data, response.etag, game)
          // A replay can contain old totals. Confirm deletion and read current totals.
          try { await get(id); throw new Error('削除後の状態を確認できませんでした。最新の状態を確認してください。') }
          catch (error) { if (!(error instanceof ApiError && error.status === 410)) throw error }
          await totals()
          storage.removeItem(commandKey(id))
          return null
        }
        const result = mapGame(suffix === '/payment' ? object(response.data).game : response.data, response.etag)
        if (result.id !== id || result.revision <= revision) throw new Error('ゲームの応答を確認できませんでした。')
        if (suffix === '/payment') {
          if (result.status !== 'completed' || result.payment?.totalYen !== body?.totalYen) throw new Error('保存結果を確認できませんでした。')
          mapLossTotals(object(response.data).lossTotals)
        }
        if (suffix === '/cancel' && result.status !== 'cancelled') throw new Error('中止結果を確認できませんでした。')
        // Replayed responses can be old; render only the latest authoritative game.
        const latest = await get(id)
        if (latest.revision < result.revision) throw new Error('最新のゲームを確認できませんでした。もう一度状態を確認してください。')
        if (suffix === '/payment') await totals()
        storage.removeItem(commandKey(id)); previews.delete(id)
        return latest
      } catch (error) {
        if (error instanceof ApiError && [400, 403, 404, 409, 410, 412, 422, 428].includes(error.status)) {
          storage.removeItem(commandKey(id))
          if (error.status === 409 || error.status === 412) {
            previews.delete(id)
            try { await get(id) } catch { /* Keep the original conflict message. */ }
          }
        }
        throw error
      }
    } finally { updating.delete(id) }
  }
  async function dice(id: string, revision: number, nextRound: boolean, participantId?: string): Promise<Game> {
    const game = games.get(id) ?? await get(id)
    const roundNumber = currentRound(game).number
    const body = nextRound ? { previousRoundNumber: roundNumber } : participantId === undefined ? { roundNumber, mode: 'remaining' } : { roundNumber, mode: 'individual', participantId }
    return (await mutate(id, revision, nextRound ? '/rounds' : '/rolls', body))!
  }
  let creating = false
  return {
    mode: 'api',
    async participants() { return mapParticipants((await client.request('participants')).data) },
    totals,
    async active() {
      const response = await client.request('games/active'), game = object(response.data).game
      if (game === null) return null
      const active = mapGame(game, response.etag)
      games.set(active.id, active)
      const pending = pendingCommand(active.id)
      if (pending && active.revision > pending.revision) storage.removeItem(commandKey(active.id))
      return active
    },
    get,
    pendingParticipants() {
      const raw = storage.getItem(pendingKey)
      if (!raw) return null
      const pending = object(JSON.parse(raw))
      if (!Array.isArray(pending.ids) || pending.ids.some(id => typeof id !== 'string')) throw new Error('送信待ちの記録を読み込めません。')
      return pending.ids as string[]
    },
    async history() { throw new Error('履歴はページ単位で取得してください。') },
    async historyPage(cursor) {
      const query = new URLSearchParams({ limit: '20' })
      if (cursor !== undefined) query.set('cursor', cursor)
      return mapHistory((await client.request(`games?${query}`)).data)
    },
    async historyDetail(id) {
      const response = await client.request(`games/${encodeURIComponent(id)}/history-detail`)
      const game = mapGame(response.data, response.etag, true)
      if (game.id !== id) throw new Error('履歴の応答を確認できませんでした。')
      games.set(id, game)
      const pending = pendingCommand(id)
      if (pending && game.revision > pending.revision) storage.removeItem(commandKey(id))
      return game
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
      previews.set(id, { revision: game.revision, totalYen, etag: response.etag! })
      return payment
    },
    roll: (id, revision, participantId) => dice(id, revision, false, participantId),
    nextRound: (id, revision) => dice(id, revision, true),
    async save(id, revision, totalYen) {
      parseTotalYen(String(totalYen))
      const preview = previews.get(id)
      const pending = pendingCommand(id)
      // A retained payment command is also valid after a reload with a lost response.
      const retry = pending?.path === `games/${encodeURIComponent(id)}/payment` && pending.revision === revision && pending.body?.totalYen === totalYen
      if (!retry && (!preview || preview.revision !== revision || preview.totalYen !== totalYen || preview.etag !== `"g-${id}-v${revision}"`)) throw new Error('負担額を再確認してから保存してください。')
      return (await mutate(id, revision, '/payment', { totalYen }))!
    },
    async cancel(id, revision) { await mutate(id, revision, '/cancel', {}) },
    async delete(id, revision) { await mutate(id, revision, '', undefined, 'DELETE') },
    async resetDemo() { throw new Error('APIモードではデモデータを初期化できません。') },
  }
}
