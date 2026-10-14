import { describe, expect, it } from 'vitest'
import { currentRound } from '../domain/game'
import { createMockService, STORAGE_KEY } from './mock-service'

function fixture(values = [4, 6, 2, 5]) {
  const data = new Map<string, string>()
  const storage = { getItem: (key: string) => data.get(key) ?? null, setItem: (key: string, value: string) => { data.set(key, value) } }
  let index = 0
  return { data, storage, service: createMockService(storage, () => values[index++ % values.length]) }
}
const ids = ['p001', 'p002', 'p003', 'p004']

describe('デモのゲーム操作と保存', () => {
  it('全員表示と未参加・0円を区別する', async () => {
    const { service } = fixture(), totals = await service.totals()
    expect(totals).toHaveLength(6)
    expect(totals.find(p => p.id === 'p004')).toMatchObject({ hasParticipated: true, totalYen: 0n })
    expect(totals.find(p => p.id === 'p005')).toMatchObject({ hasParticipated: false, totalYen: 0n })
  })
  it('個別→一括で既存の出目を保持し、再読み込みで復元する', async () => {
    const { service, storage } = fixture()
    let game = await service.create(ids)
    game = await service.roll(game.id, game.revision, 'p001')
    expect(currentRound(game).rolls.p001).toBe(4)
    const resumed = await createMockService(storage).active()
    expect(resumed).toEqual(game)
    game = await service.roll(game.id, game.revision)
    expect(currentRound(game).rolls).toEqual({ p001: 4, p002: 6, p003: 2, p004: 5 })
    expect(game.loserId).toBe('p003')
    await expect(service.roll(game.id, game.revision)).rejects.toThrow()
  })
  it('同点から全員を未抽選に戻し、前ラウンドを保持する', async () => {
    const { service } = fixture([1, 1, 6, 5])
    let game = await service.create(ids)
    game = await service.roll(game.id, game.revision)
    expect(game.status).toBe('tie')
    game = await service.nextRound(game.id, game.revision)
    expect(game.status).toBe('rolling')
    expect(game.rounds[0].rolls.p001).toBe(1)
    expect(Object.values(currentRound(game).rolls)).toEqual([null, null, null, null])
  })
  it('古い版・二重保存・二重削除で累積額を変更しない', async () => {
    const { service } = fixture()
    let game = await service.create(ids)
    await expect(service.create(ids)).rejects.toThrow()
    const originalRevision = game.revision
    game = await service.roll(game.id, game.revision)
    await expect(service.roll(game.id, originalRevision)).rejects.toThrow()
    game = await service.save(game.id, game.revision, 13800)
    await expect(service.save(game.id, game.revision, 13800)).rejects.toThrow()
    expect((await service.totals()).find(p => p.id === 'p003')!.totalYen).toBe(3600n)
    await service.delete(game.id, game.revision)
    await expect(service.delete(game.id, game.revision)).rejects.toThrow()
    expect((await service.totals()).find(p => p.id === 'p003')!.totalYen).toBe(1800n)
  })
  it('0円履歴も保存・削除でき、削除後も参加済みを保つ', async () => {
    const { service } = fixture([1, 6])
    let game = await service.create(['p005', 'p006'])
    game = await service.roll(game.id, game.revision)
    game = await service.save(game.id, game.revision, 2000)
    expect(game.payment!.lossYen).toBe(0)
    await service.delete(game.id, game.revision)
    expect((await service.totals()).find(p => p.id === 'p005')).toMatchObject({ hasParticipated: true, totalYen: 0n })
    await expect(service.get(game.id)).rejects.toThrow()
  })
  it('中止したゲームを通常履歴に載せず、新規開始を許可', async () => {
    const { service } = fixture(), game = await service.create(ids)
    await service.cancel(game.id, game.revision)
    expect(await service.active()).toBeNull()
    expect(await service.history()).toHaveLength(4)
    await expect(service.create(ids)).resolves.toMatchObject({ status: 'rolling' })
  })
  it('初期サンプルをすべて削除すると履歴が空になり累積額が0円', async () => {
    const { service } = fixture()
    for (const game of await service.history()) await service.delete(game.id, game.revision)
    expect(await service.history()).toEqual([])
    expect((await service.totals()).every(p => p.totalYen === 0n)).toBe(true)
  })
  it('不正な保存データを黙って上書きしない', async () => {
    const { service, data } = fixture()
    data.set(STORAGE_KEY, '{bad')
    await expect(service.history()).rejects.toThrow('初期化')
    expect(data.get(STORAGE_KEY)).toBe('{bad')
    await service.resetDemo()
    expect(await service.history()).toHaveLength(4)
  })
  it('保存失敗を成功扱いせず、前のゲーム状態を保持する', async () => {
    const { service, storage } = fixture(), game = await service.create(ids)
    const broken = createMockService({ getItem: storage.getItem, setItem: () => { throw new Error('quota') } }, () => 1)
    await expect(broken.roll(game.id, game.revision)).rejects.toThrow('保存できません')
    expect(await service.get(game.id)).toEqual(game)
  })
})
