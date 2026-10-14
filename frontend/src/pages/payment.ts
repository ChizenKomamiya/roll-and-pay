import { parseTotalYen } from '../domain/payment'
import type { Page, Payment } from '../domain/types'
import { paymentSummary } from '../components/payment-summary'
import { actions, button, dateLabel, el, link, title, yen } from '../components/ui'
import type { PageContext } from '../components/ui'
import { gamePath, navigate } from '../router'
import { resumePath, state } from '../state'

export async function paymentPage(context: PageContext, id: string, saved = false): Promise<Page> {
  const game = await context.service.get(id)
  if (game.status !== 'awaitingPayment' && game.status !== 'completed') { navigate(resumePath(game), true); return { element: el('section') } }
  if (game.status === 'completed' && !saved) { navigate(gamePath(id, 'result'), true); return { element: el('section') } }
  if (game.status !== 'completed' && saved) { navigate(gamePath(id, 'payment'), true); return { element: el('section') } }
  const loser = game.participants.find(p => p.id === game.loserId)!
  const root = el('section', 'screen')
  if (saved) {
    root.append(el('div', 'saved-heading', el('span', '', '✓ 保存済み'), link('ホーム →', '/home', '')))
    const share = el('div', 'share-card', title('ROLL & PAY / RESULT', `今日の端数は、${loser.name}。`, `${dateLabel(game.createdAt)} / ${game.participants.length}人`), paymentSummary(game, game.payment!, true))
    root.append(share, el('p', 'hint', '結果部分をスクリーンショットで共有できます。'), link('履歴詳細を見る →', gamePath(id, 'detail')))
    return { element: root }
  }
  root.append(title('STEP 03 / PAYMENT', '会計を、すっきり。', `敗者は${loser.name}。会計総額を入力してください。`))
  const form = el('form'), card = el('div', 'card'), label = el('label', 'field-label', '会計総額'), input = el('input')
  label.htmlFor = input.id = 'total-yen'; input.type = 'text'; input.inputMode = 'numeric'; input.autocomplete = 'off'; input.required = true; input.maxLength = 12
  input.value = state.paymentInputs.get(id) ?? ''; input.placeholder = '例：13,800'; input.setAttribute('aria-describedby', 'total-hint')
  const hint = el('p', 'hint', '1〜9,999,999円の整数で入力してください。'); hint.id = 'total-hint'
  card.append(label, el('div', 'money-input', input, el('span', '', '円')), hint)
  const previewArea = el('div'), confirmButton = button('負担額を確認する →', () => form.requestSubmit())
  form.append(card, confirmButton, previewArea)
  const action = actions(context, form)
  let preview: Payment | null = null
  input.addEventListener('input', () => { state.paymentInputs.set(id, input.value); preview = null; previewArea.replaceChildren(); action.error.hidden = true; confirmButton.hidden = false; input.removeAttribute('aria-invalid') })
  form.addEventListener('submit', event => { event.preventDefault(); void action.run(async () => {
    let total: number
    try { total = parseTotalYen(input.value) } catch (reason) { input.setAttribute('aria-invalid', 'true'); throw reason }
    preview = await context.service.preview(id, total)
    if (!context.isCurrent()) return
    input.removeAttribute('aria-invalid'); confirmButton.hidden = true
    const saveButton = button('この結果を保存する', () => { void action.run(async () => {
      if (!preview || parseTotalYen(input.value) !== preview.totalYen) throw new Error('入力が変わりました。負担額を再確認してください。')
      await context.service.save(id, game.revision, preview.totalYen); state.paymentInputs.delete(id)
      if (context.isCurrent()) navigate(gamePath(id, 'result'))
    }) })
    previewArea.replaceChildren(el('h2', 'preview-heading', '負担額を確認。'), el('p', 'muted', 'まだ保存されていません。'), paymentSummary(game, preview), el('p', 'hint', `基準負担額${yen(preview.baseYen)} × ${game.participants.length}人 ＋ 負け額${yen(preview.lossYen)}`), saveButton)
    if (preview.baseYen === 0) previewArea.prepend(el('div', 'notice', el('strong', '', '少額会計のため敗者が全額を負担します'), el('p', '', '他の参加者の負担は0円です。金額を確認してください。')))
  }) })
  root.append(form, link('ホームへ戻る（ゲームは保持）', '/home', 'back lower-back'))
  return { element: root }
}
