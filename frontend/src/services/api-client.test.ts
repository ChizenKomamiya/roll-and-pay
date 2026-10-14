import { describe, expect, it, vi } from 'vitest'
import { ApiError, createApiClient } from './api-client'

describe('API通信の土台', () => {
  it('アクセストークン・冪等性キー・ETagを渡し、読取セッションを次要求へ継承', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(new Response('{"ok":true}', { headers: { ETag: '"v1"', 'X-Read-Session': 'read-session' } })).mockResolvedValueOnce(new Response('{}'))
    const client = createApiClient({ baseUrl: 'https://api.example.test/api/v1', getAccessToken: async () => 'access-token', fetch: fetcher })
    const result = await client.request('games', { method: 'POST', body: { participantIds: ['p001', 'p002'] }, idempotencyKey: 'operation-key' })
    expect(result.etag).toBe('"v1"')
    await client.request('games/id', { method: 'DELETE', idempotencyKey: 'delete-key', ifMatch: result.etag! })
    const init = fetcher.mock.calls[1][1]!
    const headers = new Headers(init.headers)
    expect(headers.get('Authorization')).toBe('Bearer access-token')
    expect(headers.get('X-Read-Session')).toBe('read-session')
    expect(headers.get('If-Match')).toBe('"v1"')
    expect(headers.get('Idempotency-Key')).toBe('delete-key')
    expect(init.body).toBeUndefined()
    expect(init.redirect).toBe('error')
  })
  it('削除済みエラーをHTTP statusとcodeで返す', async () => {
    const client = createApiClient({ baseUrl: 'https://api.example.test/api/v1', getAccessToken: async () => 'token', fetch: vi.fn<typeof fetch>().mockResolvedValue(new Response('{"error":{"code":"GAME_DELETED"}}', { status: 410 })) })
    await expect(client.request('games/deleted')).rejects.toMatchObject({ status: 410, code: 'GAME_DELETED' } satisfies Partial<ApiError>)
  })
  it('応答断で更新要求を勝手に再送しない', async () => {
    const fetcher = vi.fn<typeof fetch>().mockRejectedValue(new TypeError('network'))
    const client = createApiClient({ baseUrl: 'http://localhost:7071/api/v1', getAccessToken: async () => 'token', fetch: fetcher })
    await expect(client.request('games', { method: 'POST', body: {}, idempotencyKey: 'key' })).rejects.toThrow()
    expect(fetcher).toHaveBeenCalledTimes(1)
  })
  it('他オリジンやAPIの範囲外へトークンを送信しない', async () => {
    const fetcher = vi.fn<typeof fetch>()
    const client = createApiClient({ baseUrl: 'https://api.example.test/api/v1', getAccessToken: async () => 'token', fetch: fetcher })
    await expect(client.request('https://other.example.test')).rejects.toThrow()
    await expect(client.request('../outside')).rejects.toThrow()
    expect(fetcher).not.toHaveBeenCalled()
    expect(() => createApiClient({ baseUrl: 'http://api.example.test', getAccessToken: async () => 'token' })).toThrow()
  })
})
