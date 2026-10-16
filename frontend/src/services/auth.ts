import { InteractionRequiredAuthError, PublicClientApplication } from '@azure/msal-browser'
import type { ApiConfig } from '../config'

export class SignInRequired extends Error {
  constructor() { super('ログインの有効期限が切れました。再度Microsoftでログインしてください。') }
}

export async function createAuth(config: ApiConfig) {
  const client = new PublicClientApplication({
    auth: { clientId: config.clientId, authority: `https://login.microsoftonline.com/${config.tenantId}`, redirectUri: new URL('redirect.html', document.baseURI).href },
    cache: { cacheLocation: 'sessionStorage' },
  })
  await client.initialize()
  const account = () => client.getActiveAccount()
  // MSAL keeps the active account across a reload; never choose an arbitrary cached user.
  return {
    accountId: () => account()?.homeAccountId ?? null,
    async login() {
      const result = await client.loginPopup({ scopes: [config.scope], prompt: 'select_account' })
      client.setActiveAccount(result.account)
    },
    async token(forceRefresh = false) {
      const current = account()
      if (!current) throw new SignInRequired()
      try { return (await client.acquireTokenSilent({ account: current, scopes: [config.scope], forceRefresh })).accessToken }
      catch (error) { if (error instanceof InteractionRequiredAuthError) throw new SignInRequired(); throw new Error('アクセストークンを取得できませんでした。再度お試しください。') }
    },
    async logout() {
      const current = account()
      client.setActiveAccount(null)
      await client.logoutPopup({ account: current, postLogoutRedirectUri: new URL('redirect.html', document.baseURI).href, mainWindowRedirectUri: new URL('./', document.baseURI).href })
    },
  }
}
