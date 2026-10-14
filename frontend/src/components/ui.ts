import type { GameService } from '../services/game-service'

export type Child = Node | string | null
export function el<K extends keyof HTMLElementTagNameMap>(tag: K, className = '', ...children: Child[]): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag)
  node.className = className
  children.forEach(child => { if (child !== null) node.append(child) })
  return node
}
export function link(label: string, path: string, className = 'button secondary') {
  const node = el('a', className, label); node.href = `#${path}`; return node
}
export function button(label: string, handler: () => void, className = 'button primary') {
  const node = el('button', className, label)
  node.type = 'button'; node.dataset.focusKey = label
  node.addEventListener('click', handler)
  return node
}
export function title(eyebrow: string, heading: string, description?: string) {
  const h = el('h1', '', heading); h.tabIndex = -1
  return el('div', 'page-title', el('p', 'eyebrow', eyebrow), h, description ? el('p', 'muted', description) : null)
}
export const yen = (value: number | bigint) => `${value.toLocaleString('ja-JP')}円`
export function dateLabel(iso: string) {
  return new Intl.DateTimeFormat('ja-JP', { timeZone: 'Asia/Tokyo', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false }).format(new Date(iso))
}
export interface PageContext { service: GameService; isCurrent: () => boolean; refresh: () => void }

/** Disables every action during a mutation, and never updates a disposed page. */
export function actions(context: PageContext, root: HTMLElement) {
  const error = el('p', 'error'); error.setAttribute('role', 'alert'); error.hidden = true
  root.append(error)
  let busy = false
  return {
    error,
    async run(task: () => Promise<void>) {
      if (busy || !context.isCurrent()) return
      const focusKey = document.activeElement?.getAttribute('data-focus-key')
      if (focusKey) root.dataset.refreshFocusKey = focusKey
      busy = true; error.hidden = true; root.setAttribute('aria-busy', 'true')
      const controls = [...root.querySelectorAll<HTMLInputElement | HTMLButtonElement>('button, input')]
      const previous = controls.map(control => control.disabled)
      controls.forEach(control => { control.disabled = true })
      try { await task() } catch (reason) {
        if (context.isCurrent()) { error.textContent = reason instanceof Error ? reason.message : '操作に失敗しました。再度お試しください。'; error.hidden = false }
      } finally {
        busy = false
        if (context.isCurrent()) { controls.forEach((control, i) => { control.disabled = previous[i] }); root.removeAttribute('aria-busy'); delete root.dataset.refreshFocusKey }
      }
    },
  }
}

export function confirmDialog(heading: string, description: string, confirm: () => Promise<void>, context: PageContext) {
  const dialog = el('dialog', 'confirm-dialog')
  const headingNode = el('h2', '', heading); headingNode.id = 'confirm-heading'
  dialog.setAttribute('aria-labelledby', headingNode.id)
  const content = el('div', '', headingNode, el('p', '', description))
  const action = actions(context, content)
  const cancel = button('キャンセル', () => dialog.close(), 'button secondary')
  cancel.autofocus = true
  content.append(button('中止する', () => { void action.run(async () => { await confirm(); dialog.close() }) }, 'button danger'), cancel)
  dialog.append(content)
  dialog.addEventListener('close', () => dialog.remove(), { once: true })
  document.body.append(dialog); dialog.showModal()
  return dialog
}
