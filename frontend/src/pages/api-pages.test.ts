// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApiClient } from '../services/api-client'
import { createApiService } from '../services/api-service'
import { completedDto, deletedDto, totalsDto, gameDto, gameId, memoryStorage, participants, paymentDto, response } from '../services/api-fixtures.test-helper'
import { homePage } from './home'
import { participantsPage } from './participants'
import { historyPage } from './history'
import { historyDetailPage } from './history-detail'
import { paymentPage } from './payment'
import { dicePage } from './dice'
import { state } from '../state'

vi.mock('../components/dice-view', () => ({ diceView: (value: number | null) => ({ element: document.createTextNode(String(value)), play: vi.fn(), dispose: vi.fn() }) }))
afterEach(() => { vi.restoreAllMocks(); Reflect.deleteProperty(HTMLDialogElement.prototype, 'showModal'); Reflect.deleteProperty(HTMLDialogElement.prototype, 'close'); location.hash = ''; document.body.replaceChildren(); state.selectedIds.clear(); state.paymentInputs.clear() })
function setup() {
  const fetcher = vi.fn<typeof fetch>()
  const service = createApiService(createApiClient({ baseUrl: 'https://api.example.test/api/v1/', getAccessToken: async () => 'token', fetch: fetcher }), memoryStorage(), 'pending')
  return { fetcher, context: { service, isCurrent: () => true, refresh: vi.fn() } }
}
describe('API接続時の画面', () => {
  it('ホームにAPIの累積額・0円・無効者と中止ボタンを表示する', async () => {
    const { context, fetcher } = setup()
    fetcher.mockResolvedValueOnce(response(totalsDto)).mockResolvedValueOnce(response({ game: gameDto() }))
    const page = await homePage(context)
    expect(page.element.textContent).toContain('9,007,199,254,740,993円')
    expect(page.element.textContent).toContain('0円')
    expect(page.element.textContent).toContain('現在は無効')
    expect(page.element.textContent).toContain('ゲームを再開')
    expect(page.element.textContent).not.toMatch(/デモデータ|準備中|未参加/)
    expect([...page.element.querySelectorAll('button')].some(button => button.textContent === 'このゲームを中止')).toBe(true)
    expect(fetcher).toHaveBeenCalledTimes(2)
  })
  it('ホームの中止確認でキャンセルは要求を送らず、確定時だけ中止する', async () => {
    const { context, fetcher } = setup()
    Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value(this: HTMLDialogElement) { this.open = true } })
    Object.defineProperty(HTMLDialogElement.prototype, 'close', { configurable: true, value(this: HTMLDialogElement) { this.open = false; this.dispatchEvent(new Event('close')) } })
    fetcher.mockResolvedValueOnce(response(totalsDto)).mockResolvedValueOnce(response({ game: gameDto() }))
    const page = await homePage(context); document.body.append(page.element)
    const open = [...page.element.querySelectorAll('button')].find(button => button.textContent === 'このゲームを中止')!
    open.click()
    ;[...document.querySelector('dialog')!.querySelectorAll('button')].find(button => button.textContent === 'キャンセル')!.click()
    expect(fetcher).toHaveBeenCalledTimes(2)
    const cancelled = gameDto({ revision: 2, status: 'cancelled' })
    fetcher.mockResolvedValueOnce(response(cancelled, 2)).mockResolvedValueOnce(response(cancelled, 2))
    open.click()
    const confirm = [...document.querySelector('dialog')!.querySelectorAll('button')].find(button => button.textContent === '中止する')!
    confirm.click(); confirm.click()
    await vi.waitFor(() => expect(context.refresh).toHaveBeenCalledOnce())
    expect(fetcher.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(1)
    page.dispose?.(); vi.restoreAllMocks()
  })
  it('参加者APIの実際の名前を表示し、2人未満では開始できない', async () => {
    const { context, fetcher } = setup()
    fetcher.mockResolvedValueOnce(response({ game: null })).mockResolvedValueOnce(response({ participants: participants.slice(0, 1) }))
    const page = await participantsPage(context)
    expect(page.element.textContent).toContain('当時の名前A')
    expect(page.element.textContent).toContain('2人以上必要')
    expect(page.element.querySelector('button')!.disabled).toBe(true)
  })
  it('未抽選者の操作を有効にし、抽選済みの出目を変更できない', async () => {
    const { context, fetcher } = setup()
    fetcher.mockResolvedValueOnce(response(gameDto()))
    const page = await dicePage(context, gameId)
    expect([...page.element.querySelectorAll<HTMLButtonElement>('.dice-card')].map(button => button.disabled)).toEqual([true, false])
    expect(page.element.textContent).not.toContain('抽選・振り直しは準備中')
    expect(page.element.textContent).toContain('残り1人をまとめて振る')
    page.dispose?.()
  })
  it('引き分けで全員の振り直しを表示し、連打でも1回だけ次ラウンドを作る', async () => {
    const { context, fetcher } = setup()
    const tied = gameDto({ status: 'tie', currentRound: { roundNumber: 3, status: 'tied', rolls: [{ participantId: 'p001', value: 2 }, { participantId: 'p002', value: 2 }] } })
    const next = gameDto({ revision: 2, currentRound: { roundNumber: 4, status: 'open', rolls: [{ participantId: 'p001', value: null }, { participantId: 'p002', value: null }] } })
    fetcher.mockResolvedValueOnce(response(tied)).mockResolvedValueOnce(response(next, 2)).mockResolvedValueOnce(response(next, 2)).mockResolvedValueOnce(response(next, 2))
    const page = await dicePage(context, gameId); document.body.append(page.element)
    expect(page.element.textContent).toContain('参加者全員で振り直します')
    expect(page.element.textContent).not.toContain('準備中')
    const reroll = [...page.element.querySelectorAll('button')].find(button => button.textContent === '全員で振り直す →')!
    reroll.click(); reroll.click()
    await vi.waitFor(() => expect(context.refresh).toHaveBeenCalledOnce())
    expect(fetcher.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(1)
    page.dispose?.()
    const refreshed = await dicePage(context, gameId)
    expect(refreshed.element.querySelector('[role=status]')!.textContent).toContain('まだ振っていません')
    expect([...refreshed.element.querySelectorAll<HTMLButtonElement>('.dice-card')].every(button => !button.disabled)).toBe(true)
    expect(refreshed.element.textContent).toContain('全員まとめて振る')
    refreshed.dispose?.()
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
  it('履歴詳細APIを使い、削除確認へのリンクを表示し抽選記録を取得しない', async () => {
    const { context, fetcher } = setup()
    fetcher.mockResolvedValueOnce(response({ ...gameDto(), loserParticipantId: 'p001', payment: paymentDto, completedAt: '2026-10-14T05:10:00Z', canDelete: true }))
    const page = await historyDetailPage(context, gameId)
    expect(page.element.textContent).toContain('当時の名前A')
    expect(page.element.textContent).not.toContain('準備中')
    expect(page.element.querySelector('a[href$="/delete"]')).not.toBeNull()
    expect(page.element.textContent).not.toContain('出目')
  })
  it('金額プレビューを確認して保存でき、連打で重複要求しない', async () => {
    const { context, fetcher } = setup()
    const dto = gameDto({ status: 'awaitingPayment', loserParticipantId: 'p001' })
    fetcher.mockResolvedValueOnce(response(dto)).mockResolvedValueOnce(response(dto)).mockResolvedValueOnce(response({ gameId, revision: 1, payment: paymentDto }))
    const page = await paymentPage(context, gameId); document.body.append(page.element)
    expect(page.element.querySelector('h1')!.textContent).toBe('当時の名前A の支払いに決定')
    expect(page.element.querySelector('.page-title .muted')!.textContent).toBe('会計総額を入力してください')
    expect(page.element.querySelector('#total-hint')!.textContent).toBe('1〜9,999,999円の整数で入力してください。')
    page.element.querySelector('input')!.value = '7800'
    page.element.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    await vi.waitFor(() => expect(page.element.textContent).toContain('この結果を保存する'))
    expect(page.element.querySelector('input')!.closest('[hidden]')).not.toBeNull()
    expect(document.activeElement?.textContent).toBe('負担額を確認。')
    expect(page.element.querySelector('.loss-highlight small')).toBeNull()
    expect(page.element.textContent).toContain('1,800円')
    expect(page.element.textContent).not.toContain('準備中')
    fetcher.mockResolvedValueOnce(response({ game: completedDto(), lossTotals: totalsDto }, 2)).mockResolvedValueOnce(response(completedDto(), 2)).mockResolvedValueOnce(response(totalsDto))
    const save = [...page.element.querySelectorAll('button')].find(button => button.textContent === 'この結果を保存する')!
    save.click(); save.click()
    await vi.waitFor(() => expect(location.hash).toBe(`#/games/${gameId}/result`))
    expect(fetcher.mock.calls.filter(([url]) => String(url).endsWith('/payment'))).toHaveLength(1)
  })
  it('再入力で確認結果を破棄し、新しい金額を再確認して保存する', async () => {
    const { context, fetcher } = setup(), dto = gameDto({ status: 'awaitingPayment', loserParticipantId: 'p001' })
    fetcher.mockResolvedValueOnce(response(dto)).mockResolvedValueOnce(response(dto)).mockResolvedValueOnce(response({ gameId, revision: 1, payment: paymentDto }))
    const page = await paymentPage(context, gameId); document.body.append(page.element)
    const input = page.element.querySelector('input')!, form = page.element.querySelector('form')!
    const submit = () => form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    const findButton = (label: string) => [...page.element.querySelectorAll('button')].find(button => button.textContent === label)!
    input.value = '7800'; input.dispatchEvent(new Event('input')); submit()
    await vi.waitFor(() => expect(findButton('会計総額を再入力する').hidden).toBe(false))
    const oldSave = findButton('この結果を保存する')
    findButton('会計総額を再入力する').click()
    expect(input.closest('[hidden]')).toBeNull()
    expect(input.value).toBe('7800')
    expect(document.activeElement).toBe(input)
    expect(page.element.querySelector('.result-card')).toBeNull()
    expect(findButton('会計総額を再入力する').hidden).toBe(true)
    oldSave.click()
    await vi.waitFor(() => expect(form.getAttribute('aria-busy')).toBeNull())
    expect(fetcher).toHaveBeenCalledTimes(3)
    input.value = '0'; input.dispatchEvent(new Event('input')); submit()
    await vi.waitFor(() => expect(input.getAttribute('aria-invalid')).toBe('true'))
    expect(input.closest('[hidden]')).toBeNull()
    expect(fetcher).toHaveBeenCalledTimes(3)
    const updatedPayment = { ...paymentDto, totalYen: 8000, baseYen: 4000, lossYen: 0, loserPaymentYen: 4000, collectFromOthersYen: 4000, shares: paymentDto.shares.map(share => ({ ...share, amountYen: 4000 })) }
    fetcher.mockResolvedValueOnce(response(dto)).mockResolvedValueOnce(response({ gameId, revision: 1, payment: updatedPayment }))
    input.value = '8000'; input.dispatchEvent(new Event('input')); submit()
    await vi.waitFor(() => expect(findButton('会計総額を再入力する').hidden).toBe(false))
    expect(page.element.querySelector('.result-top strong')!.textContent).toBe('8,000円')
    expect(state.paymentInputs.get(gameId)).toBe('8000')
    const completed = { ...completedDto(), payment: { ...updatedPayment, registeredAt: '2026-10-17T00:00:00Z' } }
    fetcher.mockResolvedValueOnce(response({ game: completed, lossTotals: totalsDto }, 2)).mockResolvedValueOnce(response(completed, 2)).mockResolvedValueOnce(response(totalsDto))
    findButton('この結果を保存する').click()
    expect(findButton('会計総額を再入力する').disabled).toBe(true)
    await vi.waitFor(() => expect(location.hash).toBe(`#/games/${gameId}/result`))
    const saveRequest = fetcher.mock.calls.find(([url]) => String(url).endsWith('/payment'))!
    expect(JSON.parse(String(saveRequest[1]!.body)).totalYen).toBe(8000)
    expect(state.paymentInputs.has(gameId)).toBe(false)
  })
  it('保存失敗後も確認画面を保持し、同じ要求で再試行できる', async () => {
    const { context, fetcher } = setup(), dto = gameDto({ status: 'awaitingPayment', loserParticipantId: 'p001' })
    fetcher.mockResolvedValueOnce(response(dto)).mockResolvedValueOnce(response(dto)).mockResolvedValueOnce(response({ gameId, revision: 1, payment: paymentDto }))
    const page = await paymentPage(context, gameId); document.body.append(page.element)
    page.element.querySelector('input')!.value = '7800'
    page.element.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    await vi.waitFor(() => expect(page.element.textContent).toContain('この結果を保存する'))
    const save = [...page.element.querySelectorAll('button')].find(button => button.textContent === 'この結果を保存する')!
    fetcher.mockRejectedValueOnce(new TypeError('network'))
    save.click()
    await vi.waitFor(() => expect(page.element.querySelector('[role=alert]')!.textContent).toContain('network'))
    expect(page.element.querySelector('input')!.closest('[hidden]')).not.toBeNull()
    expect(page.element.querySelector('.result-card')!.closest('[hidden]')).toBeNull()
    fetcher.mockResolvedValueOnce(response({ game: completedDto(), lossTotals: totalsDto }, 2)).mockResolvedValueOnce(response(completedDto(), 2)).mockResolvedValueOnce(response(totalsDto))
    save.click()
    await vi.waitFor(() => expect(location.hash).toBe(`#/games/${gameId}/result`))
    const requests = fetcher.mock.calls.filter(([url]) => String(url).endsWith('/payment'))
    expect(requests).toHaveLength(2)
    expect(requests[0][1]!.body).toBe(requests[1][1]!.body)
    expect([...new Headers(requests[0][1]!.headers)]).toEqual([...new Headers(requests[1][1]!.headers)])
  })
  it('削除確認の表示・キャンセルでは削除せず、確定クリック時だけDELETEする', async () => {
    const { context, fetcher } = setup()
    fetcher.mockResolvedValueOnce(response(completedDto(), 2))
    const page = await historyDetailPage(context, gameId, true); document.body.append(page.element)
    expect(page.element.textContent).toContain('この操作は元に戻せません')
    expect(page.element.querySelector('a.button.secondary')?.getAttribute('href')).toBe(`#/games/${gameId}/detail`)
    expect(fetcher).toHaveBeenCalledTimes(1)
    fetcher.mockResolvedValueOnce(response(deletedDto(), 3)).mockResolvedValueOnce(new Response('{}', { status: 410 })).mockResolvedValueOnce(response(totalsDto))
    const remove = [...page.element.querySelectorAll('button')].find(button => button.textContent === '削除する')!
    remove.click(); remove.click()
    await vi.waitFor(() => expect(location.hash).toBe('#/history/deleted'))
    expect(fetcher.mock.calls.filter(([, init]) => init?.method === 'DELETE')).toHaveLength(1)
  })
  it('保存競合の案内から最新状態を再取得できる', async () => {
    const { context, fetcher } = setup(), dto = gameDto({ status: 'awaitingPayment', loserParticipantId: 'p001' })
    fetcher.mockResolvedValueOnce(response(dto)).mockResolvedValueOnce(response(dto)).mockResolvedValueOnce(response({ gameId, revision: 1, payment: paymentDto }))
    const page = await paymentPage(context, gameId); document.body.append(page.element)
    page.element.querySelector('input')!.value = '7800'
    page.element.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    await vi.waitFor(() => expect(page.element.textContent).toContain('この結果を保存する'))
    fetcher.mockResolvedValueOnce(new Response('{}', { status: 412 })).mockResolvedValueOnce(response(completedDto(), 2))
    ;[...page.element.querySelectorAll('button')].find(button => button.textContent === 'この結果を保存する')!.click()
    await vi.waitFor(() => expect(page.element.querySelector('[role=alert]')!.textContent).toContain('更新されました'))
    ;[...page.element.querySelectorAll('button')].find(button => button.textContent === '最新の状態を確認')!.click()
    expect(context.refresh).toHaveBeenCalledOnce()
  })
})
