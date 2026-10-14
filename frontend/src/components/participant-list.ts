import type { Participant } from '../domain/types'
import { el } from './ui'

export function participantList(participants: Participant[], selected: Set<string>, changed: () => void) {
  const list = el('div', 'members')
  participants.forEach(participant => {
    const checkbox = el('input'); checkbox.type = 'checkbox'; checkbox.checked = selected.has(participant.id)
    checkbox.addEventListener('change', () => { if (checkbox.checked) selected.add(participant.id); else selected.delete(participant.id); changed() })
    list.append(el('label', '', el('span', 'avatar', participant.name.slice(0, 1)), el('strong', '', participant.name), checkbox))
  })
  return list
}
