export const gameId = '7c56e8d0-1a58-4c45-8ef3-6505ad5c0a01'
export const participants = [{ participantId: 'p001', displayName: '当時の名前A', order: 1 }, { participantId: 'p002', displayName: '当時の名前B', order: 2 }]
export function gameDto(overrides: Record<string, unknown> = {}) {
  return { gameId, revision: 1, status: 'rolling', createdAt: '2026-10-14T05:00:00Z', updatedAt: '2026-10-14T05:00:00Z', completedAt: null, cancelledAt: null,
    participants, currentRound: { roundNumber: 3, status: 'open', rolls: [{ participantId: 'p001', value: 2 }, { participantId: 'p002', value: null }] }, loserParticipantId: null, payment: null, ...overrides }
}
export const paymentDto = { totalYen: 7800, participantCount: 2, baseYen: 3000, lossYen: 1800, loserParticipantId: 'p001', loserPaymentYen: 4800, collectFromOthersYen: 3000,
  shares: [{ participantId: 'p001', amountYen: 4800 }, { participantId: 'p002', amountYen: 3000 }], calculationVersion: 'thousand-remainder-v1', registeredAt: null }
export function response(data: unknown, revision = 1, headers: Record<string, string> = {}) {
  return new Response(JSON.stringify(data), { headers: { ETag: `"g-${gameId}-v${revision}"`, ...headers } })
}
export function memoryStorage() {
  const data = new Map<string, string>()
  return { getItem: (key: string) => data.get(key) ?? null, setItem: (key: string, value: string) => { data.set(key, value) }, removeItem: (key: string) => { data.delete(key) } }
}
