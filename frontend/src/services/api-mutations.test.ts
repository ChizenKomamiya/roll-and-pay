import { describe, expect, it, vi } from 'vitest'
import { createApiClient } from './api-client'
import { createApiService } from './api-service'
import { mapLossTotals } from './api-models'
import { awaitingDto, completedDto, deletedDto, gameDto, gameId, memoryStorage, paymentDto, response, totalsDto } from './api-fixtures.test-helper'

function setup(storage = memoryStorage()) {
  const fetcher = vi.fn<typeof fetch>()
  const service = createApiService(createApiClient({ baseUrl: 'https://api.example.test/api/v1/', getAccessToken: async () => 'token', fetch: fetcher }), storage, 'pending')
  return { fetcher, service, storage }
}
const gone = () => new Response('{"error":{"code":"GAME_DELETED"}}', { status: 410 })
const cancelDto = (revision = 2) => gameDto({ revision, status: 'cancelled', cancelledAt: '2026-10-17T00:00:00Z' })
const options = (call: Parameters<typeof fetch>) => ({ body: call[1]!.body, method: call[1]!.method, key: new Headers(call[1]!.headers).get('Idempotency-Key'), etag: new Headers(call[1]!.headers).get('If-Match') })
async function preview(s: ReturnType<typeof setup>) {
  s.fetcher.mockResolvedValueOnce(response(awaitingDto())).mockResolvedValueOnce(response({ gameId, revision: 1, payment: paymentDto }))
  await s.service.preview(gameId, 7800)
}

describe('保存・中止・削除・累積額のAPI接続', () => {
  it('累積額を数値に丸めずBigIntへ変換し、無効者・0円・参加実績不明を保持する', async () => {
    const { fetcher, service } = setup()
    fetcher.mockResolvedValueOnce(response(totalsDto))
    expect(await service.totals()).toEqual([
      { id: 'p001', name: '現在の名前A', totalYen: 9007199254740993n, hasParticipated: null, isActive: true },
      { id: 'p002', name: '現在の名前B', totalYen: 0n, hasParticipated: null, isActive: false },
    ])
    expect(String(fetcher.mock.calls[0][0])).toContain('/statistics/losses')
  })
  it.each(['-1', '01', '1.5', '1e3', '9223372036854775808', 1800])('不正な累積額 %s を表示しない', amount => {
    expect(() => mapLossTotals({ ...totalsDto, participants: [{ ...totalsDto.participants[0], totalLossYen: amount }] })).toThrow('形式')
  })
  it('重複参加者・不正な日時を拒否し、初期の0円を受け付ける', () => {
    expect(() => mapLossTotals({ ...totalsDto, participants: [totalsDto.participants[0], totalsDto.participants[0]] })).toThrow('形式')
    expect(() => mapLossTotals({ ...totalsDto, updatedAt: 'bad' })).toThrow('形式')
    expect(mapLossTotals({ participants: [{ ...totalsDto.participants[1], isActive: true }], updatedAt: null })[0].totalYen).toBe(0n)
  })
  it('確認済みプレビューの総額・ETagで保存し、最新のゲームと累積額を取り直す', async () => {
    const s = setup(); await preview(s)
    s.fetcher.mockResolvedValueOnce(response({ game: completedDto(), lossTotals: totalsDto }, 2)).mockResolvedValueOnce(response(completedDto(), 2)).mockResolvedValueOnce(response(totalsDto))
    expect((await s.service.save(gameId, 1, 7800)).status).toBe('completed')
    expect(options(s.fetcher.mock.calls[2])).toMatchObject({ body: '{"totalYen":7800}', method: 'POST', etag: `"g-${gameId}-v1"` })
    expect(String(s.fetcher.mock.calls[2][0])).toContain('/payment')
    expect(String(s.fetcher.mock.calls[4][0])).toContain('/statistics/losses')
  })
  it('プレビューなし・別金額・別revisionでは保存要求を送らない', async () => {
    const s = setup()
    await expect(s.service.save(gameId, 1, 7800)).rejects.toThrow('再確認')
    expect(s.fetcher).not.toHaveBeenCalled()
    await preview(s)
    await expect(s.service.save(gameId, 1, 7900)).rejects.toThrow('再確認')
    await expect(s.service.save(gameId, 2, 7800)).rejects.toThrow('再確認')
    expect(s.fetcher).toHaveBeenCalledTimes(2)
  })
  it('保存の応答断後はリロードをまたいで同一本文・キー・ETagを再送する', async () => {
    const first = setup(); await preview(first)
    first.fetcher.mockRejectedValueOnce(new TypeError('network'))
    await expect(first.service.save(gameId, 1, 7800)).rejects.toThrow('network')
    const second = setup(first.storage)
    second.fetcher.mockResolvedValueOnce(response(awaitingDto())).mockResolvedValueOnce(response({ game: completedDto(), lossTotals: totalsDto }, 2, { 'Idempotency-Replayed': 'true' })).mockResolvedValueOnce(response(completedDto(4), 4)).mockResolvedValueOnce(response(totalsDto))
    expect((await second.service.save(gameId, 1, 7800)).revision).toBe(4)
    expect(options(second.fetcher.mock.calls[1])).toEqual(options(first.fetcher.mock.calls[2]))
  })
  it.each([429, 500, 503])('%sの後も保存キーを保持し、別金額や中止へ変えない', async status => {
    const s = setup(); await preview(s)
    s.fetcher.mockResolvedValueOnce(new Response('{}', { status }))
    await expect(s.service.save(gameId, 1, 7800)).rejects.toMatchObject({ status })
    await expect(s.service.cancel(gameId, 1)).rejects.toThrow('未確認')
    expect(s.storage.getItem(`pending:dice:${gameId}`)).not.toBeNull()
    expect(s.fetcher).toHaveBeenCalledTimes(3)
  })
  it('412は最新を取得してプレビューを無効化し、古い保存を自動実行しない', async () => {
    const s = setup(); await preview(s)
    s.fetcher.mockResolvedValueOnce(new Response('{}', { status: 412 })).mockResolvedValueOnce(response(completedDto(), 2))
    await expect(s.service.save(gameId, 1, 7800)).rejects.toMatchObject({ status: 412 })
    await expect(s.service.save(gameId, 1, 7800)).rejects.toThrow('再確認')
    expect(s.fetcher.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(2)
  })
  it('保存成功の古い再生後に410ならゲームを復活表示しない', async () => {
    const s = setup(); await preview(s)
    s.fetcher.mockResolvedValueOnce(response({ game: completedDto(), lossTotals: totalsDto }, 2, { 'Idempotency-Replayed': 'true' })).mockResolvedValueOnce(gone())
    await expect(s.service.save(gameId, 1, 7800)).rejects.toMatchObject({ status: 410 })
    expect(s.storage.getItem(`pending:dice:${gameId}`)).toBeNull()
  })
  it('中止は空オブジェクトを送り、サーバーの最新状態を取得する', async () => {
    const s = setup()
    s.fetcher.mockResolvedValueOnce(response(gameDto())).mockResolvedValueOnce(response(cancelDto(), 2)).mockResolvedValueOnce(response(cancelDto(), 2))
    await s.service.get(gameId); await s.service.cancel(gameId, 1)
    expect(options(s.fetcher.mock.calls[1])).toMatchObject({ body: '{}', method: 'POST', etag: `"g-${gameId}-v1"` })
    expect(String(s.fetcher.mock.calls[1][0])).toContain('/cancel')
  })
  it('ホームで取得した新しいrevisionを使って中止し、古いキャッシュで妨げない', async () => {
    const s = setup()
    s.fetcher.mockResolvedValueOnce(response(gameDto())).mockResolvedValueOnce(response({ game: gameDto({ revision: 3 }) }, 3)).mockResolvedValueOnce(response(cancelDto(4), 4)).mockResolvedValueOnce(response(cancelDto(4), 4))
    await s.service.get(gameId)
    const active = await s.service.active()
    await s.service.cancel(gameId, active!.revision)
    expect(options(s.fetcher.mock.calls[2]).etag).toBe(`"g-${gameId}-v3"`)
  })
  it('中止の再送も元のキーを使い、二重実行しない', async () => {
    const first = setup()
    first.fetcher.mockResolvedValueOnce(response(gameDto())).mockRejectedValueOnce(new TypeError('network'))
    await first.service.get(gameId); await expect(first.service.cancel(gameId, 1)).rejects.toThrow()
    const second = setup(first.storage)
    second.fetcher.mockResolvedValueOnce(response(gameDto())).mockResolvedValueOnce(response(cancelDto(), 2)).mockResolvedValueOnce(response(cancelDto(), 2))
    await second.service.cancel(gameId, 1)
    expect(options(second.fetcher.mock.calls[1])).toEqual(options(first.fetcher.mock.calls[1]))
  })
  it('履歴詳細のETagで本文なしDELETEし、410確認と最新集計を取得する', async () => {
    const s = setup()
    s.fetcher.mockResolvedValueOnce(response(completedDto(), 2)).mockResolvedValueOnce(response(deletedDto(), 3)).mockResolvedValueOnce(gone()).mockResolvedValueOnce(response(totalsDto))
    await s.service.historyDetail!(gameId); await s.service.delete(gameId, 2)
    expect(options(s.fetcher.mock.calls[1])).toMatchObject({ method: 'DELETE', body: undefined, etag: `"g-${gameId}-v2"` })
    expect(new Headers(s.fetcher.mock.calls[1][1]!.headers).has('Content-Type')).toBe(false)
    expect(String(s.fetcher.mock.calls[3][0])).toContain('/statistics/losses')
  })
  it('削除の通信断後にも履歴詳細を経て元のキー・ETagで再送する', async () => {
    const first = setup()
    first.fetcher.mockResolvedValueOnce(response(completedDto(), 2)).mockRejectedValueOnce(new TypeError('network'))
    await first.service.historyDetail!(gameId); await expect(first.service.delete(gameId, 2)).rejects.toThrow()
    const second = setup(first.storage)
    second.fetcher.mockResolvedValueOnce(response(completedDto(), 2)).mockResolvedValueOnce(response(deletedDto(), 3, { 'Idempotency-Replayed': 'true' })).mockResolvedValueOnce(gone()).mockResolvedValueOnce(response(totalsDto))
    await second.service.historyDetail!(gameId); await second.service.delete(gameId, 2)
    expect(options(second.fetcher.mock.calls[1])).toEqual(options(first.fetcher.mock.calls[1]))
  })
  it('0円の履歴も削除できる', async () => {
    const s = setup(), deleted = deletedDto()
    const game = { ...completedDto(), payment: { ...paymentDto, totalYen: 6000, lossYen: 0, loserPaymentYen: 3000, shares: [{ participantId: 'p001', amountYen: 3000 }, { participantId: 'p002', amountYen: 3000 }] } }
    s.fetcher.mockResolvedValueOnce(response(game, 2)).mockResolvedValueOnce(response({ ...deleted, removedLoss: { participantId: 'p001', amountYen: 0 } }, 3)).mockResolvedValueOnce(gone()).mockResolvedValueOnce(response(totalsDto))
    await s.service.historyDetail!(gameId); await s.service.delete(gameId, 2)
  })
  it.each(['cancel', 'delete'] as const)('要求を永続化できなければ%sの更新APIを送らない', async operation => {
    const s = setup({ ...memoryStorage(), setItem() { throw new Error('storage') } })
    s.fetcher.mockResolvedValueOnce(response(operation === 'delete' ? completedDto() : gameDto(), operation === 'delete' ? 2 : 1))
    if (operation === 'delete') await s.service.historyDetail!(gameId); else await s.service.get(gameId)
    await expect(s.service[operation](gameId, operation === 'delete' ? 2 : 1)).rejects.toThrow('storage')
    expect(s.fetcher).toHaveBeenCalledTimes(1)
  })
  it('保存中の連打や別更新は拒否する', async () => {
    const s = setup(); await preview(s)
    let resolve!: (response: Response) => void
    s.fetcher.mockImplementationOnce(() => new Promise(done => { resolve = done })).mockResolvedValueOnce(response(completedDto(), 2)).mockResolvedValueOnce(response(totalsDto))
    const saving = s.service.save(gameId, 1, 7800)
    await vi.waitFor(() => expect(s.fetcher).toHaveBeenCalledTimes(3))
    await expect(s.service.save(gameId, 1, 7800)).rejects.toThrow('操作中')
    await expect(s.service.cancel(gameId, 1)).rejects.toThrow('操作中')
    resolve(response({ game: completedDto(), lossTotals: totalsDto }, 2)); await saving
  })
})
