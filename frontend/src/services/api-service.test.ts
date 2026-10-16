import { describe, expect, it, vi } from 'vitest'
import { createApiClient } from './api-client'
import { createApiService } from './api-service'
import { gameDto, gameId, memoryStorage, participants, paymentDto, response } from './api-fixtures.test-helper'

function setup(fetcher = vi.fn<typeof fetch>(), storage = memoryStorage()) {
  const client = createApiClient({ baseUrl: 'https://api.example.test/api/v1/', getAccessToken: async () => 'token', fetch: fetcher })
  return { service: createApiService(client, storage, 'pending'), storage, fetcher }
}
describe('実APIの画面サービス', () => {
  it('参加者、未完了null、保存された現ラウンドを取得する', async () => {
    const { service, fetcher } = setup()
    fetcher.mockResolvedValueOnce(response({ participants })).mockResolvedValueOnce(response({ game: null })).mockResolvedValueOnce(response(gameDto()))
    expect(await service.participants()).toEqual([{ id: 'p001', name: '当時の名前A' }, { id: 'p002', name: '当時の名前B' }])
    expect(await service.active()).toBeNull()
    const game = await service.get(gameId)
    expect(game.rounds).toEqual([{ number: 3, rolls: { p001: 2, p002: null } }])
    expect(game.revision).toBe(1)
  })
  it('通信断と再読み込み後も同じ作成キーと本文を使用し、再生後は最新を表示する', async () => {
    const first = setup()
    first.fetcher.mockRejectedValueOnce(new TypeError('network'))
    await expect(first.service.create(['p001', 'p002'])).rejects.toThrow()
    const pending = JSON.parse(first.storage.getItem('pending')!)
    const second = setup(vi.fn<typeof fetch>(), first.storage)
    expect(second.service.pendingParticipants!()).toEqual(['p001', 'p002'])
    second.fetcher.mockResolvedValueOnce(response(gameDto(), 1, { 'Idempotency-Replayed': 'true' })).mockResolvedValueOnce(response(gameDto({ revision: 7, status: 'tie' }), 7))
    const game = await second.service.create(['p001', 'p002'])
    expect(game.revision).toBe(7)
    const init = second.fetcher.mock.calls[0][1]!
    expect(new Headers(init.headers).get('Idempotency-Key')).toBe(pending.key)
    expect(init.body).toBe(first.fetcher.mock.calls[0][1]!.body)
    expect(first.storage.getItem('pending')).toBeNull()
  })
  it('未確認の作成要求を別の本文・キーに変更しない', async () => {
    const { service, fetcher } = setup()
    fetcher.mockRejectedValueOnce(new TypeError('network'))
    await expect(service.create(['p001', 'p002'])).rejects.toThrow()
    await expect(service.create(['p002', 'p001'])).rejects.toThrow('未確認')
    expect(fetcher).toHaveBeenCalledTimes(1)
  })
  it('503では作成キーを保持し、確定した入力エラーでは破棄する', async () => {
    const { service, fetcher, storage } = setup()
    fetcher.mockResolvedValueOnce(new Response('{}', { status: 503 })).mockResolvedValueOnce(new Response('{}', { status: 422 }))
    await expect(service.create(['p001', 'p002'])).rejects.toMatchObject({ status: 503 })
    expect(storage.getItem('pending')).not.toBeNull()
    await expect(service.create(['p001', 'p002'])).rejects.toMatchObject({ status: 422 })
    expect(storage.getItem('pending')).toBeNull()
  })
  it('要求を保存できない場合はゲーム作成APIを呼ばない', async () => {
    const { service, fetcher } = setup(vi.fn<typeof fetch>(), { ...memoryStorage(), setItem() { throw new Error('storage') } })
    await expect(service.create(['p001', 'p002'])).rejects.toThrow('storage')
    expect(fetcher).not.toHaveBeenCalled()
  })
  it('カーソルをエンコードしてページングし、履歴詳細専用APIを使用する', async () => {
    const { service, fetcher } = setup()
    const item = { gameId, createdAt: '2026-10-14T05:00:00Z', loser: participants[0], participantCount: 2, totalYen: 7800, lossYen: 0 }
    fetcher.mockResolvedValueOnce(response({ items: [item], nextCursor: 'opaque+&/=' })).mockResolvedValueOnce(response({ items: [], nextCursor: null }))
      .mockResolvedValueOnce(response({ ...gameDto(), completedAt: '2026-10-14T05:10:00Z', loserParticipantId: 'p001', payment: paymentDto, canDelete: true }))
    expect((await service.historyPage!()).items[0].lossYen).toBe(0)
    await service.historyPage!('opaque+&/=')
    expect(new URL(String(fetcher.mock.calls[1][0])).searchParams.get('cursor')).toBe('opaque+&/=')
    const detail = await service.historyDetail!(gameId)
    expect(new URL(String(fetcher.mock.calls[2][0])).pathname).toBe(`/api/v1/games/${gameId}/history-detail`)
    expect(detail.rounds).toEqual([])
    expect(detail.participants[0].name).toBe('当時の名前A')
  })
  it('金額プレビューはサーバーの計算結果を表示する', async () => {
    const { service, fetcher } = setup()
    fetcher.mockResolvedValueOnce(response(gameDto({ status: 'awaitingPayment', loserParticipantId: 'p001' })))
      .mockResolvedValueOnce(response({ gameId, revision: 1, payment: paymentDto }))
    expect(await service.preview(gameId, 7800)).toMatchObject({ lossYen: 1800, loserPaymentYen: 4800 })
    expect(fetcher.mock.calls[1][1]!.body).toBe('{"totalYen":7800}')
  })
  it('ゲームrevisionが変わったプレビューは表示しない', async () => {
    const { service, fetcher } = setup()
    fetcher.mockResolvedValueOnce(response(gameDto({ status: 'awaitingPayment', loserParticipantId: 'p001' })))
      .mockResolvedValueOnce(response({ gameId, revision: 2, payment: paymentDto }, 2))
    await expect(service.preview(gameId, 7800)).rejects.toThrow('更新')
  })
  it('不正なETag・出目・支払い応答を画面へ渡さない', async () => {
    const { service, fetcher } = setup()
    fetcher.mockResolvedValueOnce(response(gameDto(), 2)).mockResolvedValueOnce(response(gameDto({ currentRound: { roundNumber: 1, rolls: [{ participantId: 'p001', value: 7 }, { participantId: 'p002', value: null }] } })))
    await expect(service.get(gameId)).rejects.toThrow('形式')
    await expect(service.get(gameId)).rejects.toThrow('形式')
  })
  it('未実装の操作は通信しない', async () => {
    const { service, fetcher } = setup()
    await expect(service.roll(gameId, 1)).rejects.toThrow('準備中')
    await expect(service.save(gameId, 1, 7800)).rejects.toThrow('準備中')
    await expect(service.cancel(gameId, 1)).rejects.toThrow('準備中')
    await expect(service.delete(gameId, 1)).rejects.toThrow('準備中')
    await expect(service.totals()).rejects.toThrow('準備中')
    expect(fetcher).not.toHaveBeenCalled()
  })
})
