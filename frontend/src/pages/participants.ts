import type { Page } from '../domain/types'
import { participantList } from '../components/participant-list'
import { actions, button, el, link, title } from '../components/ui'
import type { PageContext } from '../components/ui'
import { navigate } from '../router'
import { resumePath, state } from '../state'

export async function participantsPage(context: PageContext): Promise<Page> {
  const active = await context.service.active()
  if (active) { navigate('/home', true); return { element: el('section') } }
  const participants = await context.service.participants()
  const pending = context.service.pendingParticipants?.()
  if (pending) { state.selectedIds.clear(); pending.forEach(id => state.selectedIds.add(id)) }
  for (const id of state.selectedIds) { if (!participants.some(p => p.id === id)) state.selectedIds.delete(id) }
  const root = el('section', 'screen', link('← ホーム', '/home', 'back'), title('STEP 01 / MEMBERS', '今日は、だれと？', '参加する人にチェックを入れてください。'))
  const form = el('form'), count = el('p', 'hint')
  const start = button('このメンバーで始める →', () => form.requestSubmit())
  const update = () => { count.textContent = `${state.selectedIds.size}人を選択中 · 2〜6人で始められます。`; start.disabled = state.selectedIds.size < 2 || state.selectedIds.size > 6 }
  form.append(participantList(participants, state.selectedIds, update), count, start)
  const action = actions(context, form)
  form.addEventListener('submit', event => { event.preventDefault(); void action.run(async () => { const game = await context.service.create([...state.selectedIds]); if (context.isCurrent()) navigate(resumePath(game)) }) })
  root.append(form)
  if (pending) root.append(el('p', 'notice', '直前の作成結果が未確認です。同じメンバーで再送できます。'))
  if (participants.length < 2) root.append(el('p', 'notice', '有効な参加者が2人以上必要です。参加者の登録を確認してください。'))
  if (context.service.capabilities?.save === false) root.append(el('p', 'notice', 'ゲームの作成・抽選・振り直し・負担額の確認を利用できます。中止・金額の保存は準備中です。'))
  update()
  return { element: root }
}
