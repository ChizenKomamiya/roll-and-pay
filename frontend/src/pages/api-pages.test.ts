// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApiClient } from '../services/api-client'
import { createApiService } from '../services/api-service'
import { gameDto, gameId, memoryStorage, participants, paymentDto, response } from '../services/api-fixtures.test-helper'
import { homePage } from './home'
import { participantsPage } from './participants'
import { historyPage } from './history'
import { historyDetailPage } from './history-detail'
import { paymentPage } from './payment'
import { dicePage } from './dice'
import { state } from '../state'

vi.mock('../components/dice-view', () => ({ diceView: (value: number | null) => ({ element: document.createTextNode(String(value)), play: vi.fn(), dispose: vi.fn() }) }))
afterEach(() => { document.body.replaceChildren(); state.selectedIds.clear(); state.paymentInputs.clear() })
function setup() {
  const fetcher = vi.fn<typeof fetch>()
  const service = createApiService(createApiClient({ baseUrl: 'https://api.example.test/api/v1/', getAccessToken: async () => 'token', fetch: fetcher }), memoryStorage(), 'pending')
  return { fetcher, context: { service, isCurrent: () => true, refresh: vi.fn() } }
}
describe('API接続時の画面', () => {
  it('ホームは累積APIを呼ばず、デモ初期化とゲーム中止を表示しない', async () => {
    const { context, fetcher } = setup()
    fetcher.mockResolvedValueOnce(response({ game: gameDto() }))
    const page = await homePage(context)
    expect(page.element.textContent).toContain('累積負け額は準備中')
    expect(page.element.textContent).toContain('ゲームを再開')
    expect(page.element.textContent).not.toContain('デモデータ')
    expect(page.element.querySelector('button')).toBeNull()
    expect(fetcher).toHaveBeenCalledTimes(1)
  })
  it('参加者APIの実際の名前を表示し、2人未満では開始できない', async () => {
    const { context, fetcher } = setup()
    fetcher.mockResolvedValueOnce(response({ game: null })).mockResolvedValueOnce(response({ participants: participants.slice(0, 1) }))
    const page = await participantsPage(context)
    expect(page.element.textContent).toContain('当時の名前A')
    expect(page.element.textContent).toContain('2人以上必要')
    expect(page.element.querySelector('button')!.disabled).toBe(true)
  })
  it('抽選ボタンを無効にして保存済みの出目を表示する', async () => {
    const { context, fetcher } = setup()
    fetcher.mockResolvedValueOnce(response(gameDto()))
    const page = await dicePage(context, gameId)
    expect([...page.element.querySelectorAll<HTMLButtonElement>('.dice-card')].every(button => button.disabled)).toBe(true)
    expect(page.element.textContent).toContain('抽選・振り直しは準備中')
    expect(page.element.textContent).not.toContain('全員まとめて振る')
    page.dispose?.()
  })
  it('履歴の次ページ失敗は先頭ページを保持し、同じカーソルで再試行できる', async () => {
    const { context, fetcher } = setup()
    const item = { gameId, createdAt: '2026-10-14T05:00:00Z', loser: participants[0], participantCount: 2, totalYen: 7800, lossYen: 0 }
    fetcher.mockResolvedValueOnce(response({ items: [item], nextCursor: 'cursor' })).mockRejectedValueOnce(new TypeError('network')).mockResolvedValueOnce(response({ items: [], nextCursor: null }))
    const page = await historyPage(context); document.body.append(page.element)
    const more = page.element.querySelector('button')!
    more.click()
    await vi.waitFor(() => expect(more.disabled).toBe(false))
    expect(page.element.querySelector('.history-card')!.textContent).toContain('0円')
    expect(page.element.querySelector('[role=alert]')!.textContent).toContain('network')
    more.click()
    await vi.waitFor(() => expect(more.hidden).toBe(true))
    expect(String(fetcher.mock.calls[1][0])).toBe(String(fetcher.mock.calls[2][0]))
  })
  it('履歴詳細APIを使い、削除リンクと抽選記録を表示しない', async () => {
    const { context, fetcher } = setup()
    fetcher.mockResolvedValueOnce(response({ ...gameDto(), loserParticipantId: 'p001', payment: paymentDto, completedAt: '2026-10-14T05:10:00Z', canDelete: true }))
    const page = await historyDetailPage(context, gameId)
    expect(page.element.textContent).toContain('当時の名前A')
    expect(page.element.textContent).toContain('履歴の削除は準備中')
    expect(page.element.querySelector('a[href$="/delete"]')).toBeNull()
    expect(page.element.textContent).not.toContain('出目')
  })
  it('金額プレビューを表示し、保存ボタンを出さない', async () => {
    const { context, fetcher } = setup()
    const dto = gameDto({ status: 'awaitingPayment', loserParticipantId: 'p001' })
    fetcher.mockResolvedValueOnce(response(dto)).mockResolvedValueOnce(response(dto)).mockResolvedValueOnce(response({ gameId, revision: 1, payment: paymentDto }))
    const page = await paymentPage(context, gameId); document.body.append(page.element)
    page.element.querySelector('input')!.value = '7800'
    page.element.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    await vi.waitFor(() => expect(page.element.textContent).toContain('金額の保存は準備中'))
    expect(page.element.textContent).toContain('1,800円')
    expect([...page.element.querySelectorAll('button')].some(button => button.textContent === 'この結果を保存する')).toBe(false)
  })
})
