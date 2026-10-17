// @vitest-environment jsdom
import { describe, expect, it, vi } from 'vitest'
import type { LossTotal } from '../domain/types'
import { createMockService } from '../services/mock-service'
import { memoryStorage } from '../services/api-fixtures.test-helper'
import { homePage } from './home'

function total(id: string, amount: bigint, hasParticipated: boolean | null = true): LossTotal {
  return { id, name: id, totalYen: amount, hasParticipated }
}
async function render(totals: LossTotal[]) {
  const service = createMockService(memoryStorage())
  vi.spyOn(service, 'totals').mockResolvedValue(totals)
  vi.spyOn(service, 'active').mockResolvedValue(null)
  const page = await homePage({ service, isCurrent: () => true, refresh: vi.fn() })
  const rows = [...page.element.querySelectorAll('tbody tr')].map(row => [...row.querySelectorAll('td')].map(cell => cell.textContent))
  return { page, rows }
}

describe('ホームの累積負け額ランキング', () => {
  it('金額の降順に1人1行で表示し、同額の次順位は人数分飛ばす', async () => {
    const totals = [total('秋', 3800n), total('春', 4200n), total('冬', 3800n), total('夏', 1000n)]
    const original = [...totals]
    const { page, rows } = await render(totals)
    expect(rows).toEqual([
      ['1位', '春', '4,200円'], ['2位', '秋', '3,800円'], ['2位', '冬', '3,800円'], ['4位', '夏', '1,000円'],
    ])
    expect(totals).toEqual(original)
    expect(page.element.querySelector('.hint')!.textContent).toBe('ハイフンは参加実績なし')
  })

  it('参加済み0円を順位に含め、未参加者を順位なし・ハイフンで末尾に置く', async () => {
    const { page, rows } = await render([
      total('未参加A', 0n, false), total('ゼロA', 0n), total('同率A', 1000n),
      total('未参加B', 0n, false), total('ゼロB', 0n), total('同率B', 1000n),
    ])
    expect(rows).toEqual([
      ['1位', '同率A', '1,000円'], ['1位', '同率B', '1,000円'],
      ['3位', 'ゼロA', '0円'], ['3位', 'ゼロB', '0円'],
      ['', '未参加A', '—'], ['', '未参加B', '—'],
    ])
    expect(page.element.querySelectorAll('[aria-label="未参加"]')).toHaveLength(2)
    expect(page.element.querySelectorAll('[aria-label="順位なし"]')).toHaveLength(2)
  })

  it('全員未参加の場合は誰にも順位を付けない', async () => {
    const { rows } = await render([total('未参加A', 0n, false), total('未参加B', 0n, false)])
    expect(rows).toEqual([['', '未参加A', '—'], ['', '未参加B', '—']])
  })

  it('安全整数を超える隣接金額を区別し、参加実績不明の0円と無効者も含める', async () => {
    const inactive = { ...total('現在の名前', 0n, null), isActive: false }
    const { rows } = await render([
      inactive, total('小', 9007199254740992n, null), total('大', 9007199254740993n, null),
    ])
    expect(rows).toEqual([
      ['1位', '大', '9,007,199,254,740,993円'], ['2位', '小', '9,007,199,254,740,992円'],
      ['3位', '現在の名前（現在は無効）', '0円'],
    ])
  })
})
