import { describe, expect, it } from 'vitest'
import { calculatePayment, parseTotalYen } from './payment'

const participants = ['a', 'b', 'c', 'd'].map(id => ({ id, name: id }))
describe('支払い計算', () => {
  it('13,800円を4人で支払うと負け額だけが1,800円になる', () => {
    expect(calculatePayment(13800, participants, 'c')).toEqual({ totalYen: 13800, baseYen: 3000, lossYen: 1800, loserPaymentYen: 4800, collectFromOthersYen: 9000, shares: [{ participantId: 'a', amountYen: 3000 }, { participantId: 'b', amountYen: 3000 }, { participantId: 'c', amountYen: 4800 }, { participantId: 'd', amountYen: 3000 }] })
  })
  it.each([1, 3999, 4000, 13800, 9999999])('総額%d円の負担合計が一致し、余りが人数×1000円未満', total => {
    const result = calculatePayment(total, participants, 'a')
    expect(result.shares.reduce((sum, share) => sum + share.amountYen, 0)).toBe(total)
    expect(result.lossYen).toBeLessThan(4000)
    expect(result.baseYen % 1000).toBe(0)
  })
  it('割り切れる会計は負け額0円', () => expect(calculatePayment(12000, participants, 'a').lossYen).toBe(0))
  it('人数×1000円未満では敗者の全額負担', () => expect(calculatePayment(3999, participants, 'a').loserPaymentYen).toBe(3999))
  it.each(['', '0', '-1', '1.5', '1e3', '12,34', '10,000,000', 'Infinity'])('不正金額「%s」を拒否', text => expect(() => parseTotalYen(text)).toThrow())
  it.each(['13800', '13,800', '１３８００', ' 13800 '])('金額「%s」を解釈', text => expect(parseTotalYen(text)).toBe(13800))
  it('存在しない敗者と重複した参加者を拒否', () => {
    expect(() => calculatePayment(13800, participants, 'other')).toThrow()
    expect(() => calculatePayment(13800, [participants[0], participants[0]], 'a')).toThrow()
  })
})
