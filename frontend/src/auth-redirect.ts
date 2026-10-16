import { broadcastResponseToMainFrame } from '@azure/msal-browser/redirect-bridge'

void broadcastResponseToMainFrame().catch(() => {
  document.querySelector('#status')!.textContent = 'ログインを完了できませんでした。元の画面からもう一度ログインしてください。'
})
