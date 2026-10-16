import { describe, expect, it } from 'vitest'
import { readConfig } from './config'

export const apiEnv = { VITE_APP_MODE: 'api', VITE_API_BASE_URL: 'https://api.example.test/api/v1/', VITE_ENTRA_TENANT_ID: '11111111-1111-1111-1111-111111111111', VITE_ENTRA_SPA_CLIENT_ID: '22222222-2222-2222-2222-222222222222', VITE_ENTRA_API_SCOPE: 'api://33333333-3333-3333-3333-333333333333/access_as_user' }
describe('接続モードの設定', () => {
  it('既定はデモで、APIの公開設定を読み取る', () => {
    expect(readConfig({})).toEqual({ mode: 'demo' })
    expect(readConfig(apiEnv)).toMatchObject({ mode: 'api', baseUrl: apiEnv.VITE_API_BASE_URL })
  })
  it('API設定不足でデモへフォールバックしない', () => {
    expect(() => readConfig({ VITE_APP_MODE: 'api' })).toThrow('設定')
    expect(() => readConfig({ VITE_APP_MODE: 'typo' })).toThrow('VITE_APP_MODE')
  })
  it('HTTPの本番APIやトークンをURLに含める設定を拒否する', () => {
    for (const baseUrl of ['http://api.example.test', 'https://user:pass@api.example.test', 'https://api.example.test?token=secret']) expect(() => readConfig({ ...apiEnv, VITE_API_BASE_URL: baseUrl })).toThrow()
    expect(readConfig({ ...apiEnv, VITE_API_BASE_URL: 'http://localhost:7071/api/v1/' }).mode).toBe('api')
  })
})
