import { currentRound, judgeRolls } from '../domain/game'
import { calculatePayment } from '../domain/payment'
import type { Game, Participant, StoreDocument } from '../domain/types'
import type { GameService } from './game-service'

export const STORAGE_KEY = 'roll-and-pay.demo.v1'
export const ROSTER: Participant[] = ['あき', 'はる', 'ゆう', 'りん', 'そら', 'なつ'].map((name, index) => ({ id: `p00${index + 1}`, name }))
type StoragePort = Pick<Storage, 'getItem' | 'setItem'>

function seed(): StoreDocument {
  const samples = [
    ['2026-10-14T10:30:00Z', 'p003', 13800],
    ['2026-10-10T11:00:00Z', 'p001', 15800],
    ['2026-10-03T10:00:00Z', 'p002', 15200],
    ['2026-09-27T09:30:00Z', 'p004', 12000],
  ] as const
  return { version: 1, games: samples.map(([createdAt, loserId, total], index) => {
    const participants = structuredClone(ROSTER.slice(0, 4))
    return {
      id: `sample-${index + 1}`, revision: 1, status: 'completed', createdAt,
      completedAt: new Date(Date.parse(createdAt) + 300_000).toISOString(), participants,
      loserId, payment: calculatePayment(total, participants, loserId),
      rounds: [{ number: 1, rolls: Object.fromEntries(participants.map((p, i) => [p.id, p.id === loserId ? 1 : 3 + i % 3])) }],
    }
  }) }
}

/** Rejection sampling avoids modulo bias. Used ONLY by the browser demo service. */
export function randomDie(): number {
  const buffer = new Uint32Array(1)
  do { crypto.getRandomValues(buffer) } while (buffer[0] >= 4_294_967_292)
  return buffer[0] % 6 + 1
}

export function createMockService(storage: StoragePort, die = randomDie): GameService {
  function read(): StoreDocument {
    let raw: string | null
    try { raw = storage.getItem(STORAGE_KEY) } catch { throw new Error('このブラウザーではデモデータを読み込めません。ストレージの利用設定を確認してください。') }
    if (raw === null) { const initial = seed(); write(initial); return initial }
    try {
      const data: StoreDocument = JSON.parse(raw)
      if (data.version !== 1 || !Array.isArray(data.games) || data.games.some(g => !g.id || !Number.isInteger(g.revision) || !Array.isArray(g.participants) || !Array.isArray(g.rounds) || !['rolling', 'tie', 'awaitingPayment', 'completed', 'cancelled', 'deleted'].includes(g.status) || !Number.isFinite(Date.parse(g.createdAt)) || !g.participants.length || g.participants.some(p => typeof p.id !== 'string' || typeof p.name !== 'string') || ((g.status === 'completed' || g.status === 'deleted') && (!g.payment || !Number.isSafeInteger(g.payment.lossYen))))) throw new Error()
      return data
    } catch { throw new Error('デモデータを読み込めません。「デモデータを初期化」からサンプルに戻せます。') }
  }
  function write(data: StoreDocument) {
    try { storage.setItem(STORAGE_KEY, JSON.stringify(data)) } catch { throw new Error('保存できませんでした。ブラウザーの空き容量とストレージ設定を確認し、再度お試しください。') }
  }
  function find(data: StoreDocument, id: string): Game {
    const game = data.games.find(g => g.id === id)
    if (!game || game.status === 'deleted') throw new Error('このゲームは存在しないか、削除されています。')
    return game
  }
  function update(id: string, revision: number, allowed: Game['status'][], mutate: (game: Game) => void): Game {
    const data = read(), game = find(data, id)
    if (game.revision !== revision) throw new Error('別の操作で記録が更新されました。画面を再読み込みして確認してください。')
    if (!allowed.includes(game.status)) throw new Error('この状態では操作できません。画面を再読み込みして確認してください。')
    mutate(game)
    game.revision++
    write(data)
    return structuredClone(game)
  }
  return {
    async participants() { return structuredClone(ROSTER) },
    async totals() {
      const { games } = read()
      return ROSTER.map(p => ({ ...p,
        // Demo policy: ever created/participated, including cancelled/deleted records.
        hasParticipated: games.some(g => g.participants.some(member => member.id === p.id)),
        totalYen: games.filter(g => g.status === 'completed' && g.loserId === p.id).reduce((sum, g) => sum + BigInt(g.payment!.lossYen), 0n),
      }))
    },
    async active() { return structuredClone(read().games.find(g => ['rolling', 'tie', 'awaitingPayment'].includes(g.status)) ?? null) },
    async history() { return read().games.filter(g => g.status === 'completed').sort((a, b) => b.createdAt.localeCompare(a.createdAt) || b.id.localeCompare(a.id)) },
    async get(id) { return structuredClone(find(read(), id)) },
    async create(ids) {
      const data = read()
      if (data.games.some(g => ['rolling', 'tie', 'awaitingPayment'].includes(g.status))) throw new Error('進行中のゲームがあります。ホームから再開または中止してください。')
      if (ids.length < 2 || ids.length > 6 || new Set(ids).size !== ids.length || ids.some(id => !ROSTER.some(p => p.id === id))) throw new Error('参加者を2〜6人選択してください。')
      const participants = ids.map(id => ({ ...ROSTER.find(p => p.id === id)! }))
      const game: Game = { id: crypto.randomUUID(), revision: 1, status: 'rolling', createdAt: new Date().toISOString(), completedAt: null, participants, loserId: null, payment: null, rounds: [{ number: 1, rolls: Object.fromEntries(ids.map(id => [id, null])) }] }
      data.games.push(game); write(data); return structuredClone(game)
    },
    async roll(id, revision, participantId) {
      return update(id, revision, ['rolling'], game => {
        const round = currentRound(game)
        const targets = participantId ? [participantId] : game.participants.filter(p => round.rolls[p.id] === null).map(p => p.id)
        if (!targets.length || targets.some(id => round.rolls[id] !== null)) throw new Error('このサイコロはすでに確定しています。')
        targets.forEach(id => { const value = die(); if (!Number.isInteger(value) || value < 1 || value > 6) throw new Error('抽選結果が不正です。'); round.rolls[id] = value })
        Object.assign(game, judgeRolls(round.rolls))
      })
    },
    async nextRound(id, revision) { return update(id, revision, ['tie'], game => {
      game.rounds.push({ number: currentRound(game).number + 1, rolls: Object.fromEntries(game.participants.map(p => [p.id, null])) }); game.status = 'rolling'; game.loserId = null
    }) },
    async preview(id, totalYen) {
      const game = find(read(), id)
      if (game.status !== 'awaitingPayment' || !game.loserId) throw new Error('敗者が確定してから会計を入力してください。')
      return calculatePayment(totalYen, game.participants, game.loserId)
    },
    async save(id, revision, totalYen) { return update(id, revision, ['awaitingPayment'], game => { game.payment = calculatePayment(totalYen, game.participants, game.loserId!); game.status = 'completed'; game.completedAt = new Date().toISOString() }) },
    async cancel(id, revision) { update(id, revision, ['rolling', 'tie', 'awaitingPayment'], game => { game.status = 'cancelled' }) },
    async delete(id, revision) { update(id, revision, ['completed'], game => { game.status = 'deleted' }) },
    async resetDemo() { write(seed()) },
  }
}
