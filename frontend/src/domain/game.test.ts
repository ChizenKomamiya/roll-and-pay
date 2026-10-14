import { describe, expect, it } from 'vitest'
import { judgeRolls } from './game'

describe('敗者判定', () => {
  it('全員が振り終わるまで判定しない', () => expect(judgeRolls({ a: 1, b: null })).toEqual({ status: 'rolling', loserId: null }))
  it('最小値以外の同点では振り直さない', () => expect(judgeRolls({ a: 1, b: 3, c: 3 })).toEqual({ status: 'awaitingPayment', loserId: 'a' }))
  it('最小値の同点では敗者を確定しない', () => expect(judgeRolls({ a: 1, b: 1, c: 6 })).toEqual({ status: 'tie', loserId: null }))
  it.each([0, 7, 1.5, NaN])('不正な出目%dを拒否', value => expect(() => judgeRolls({ a: value, b: 2 })).toThrow())
})
