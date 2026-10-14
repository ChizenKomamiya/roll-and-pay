import { expect, it } from 'vitest'
import { parseRoute } from './router'

it('直接リンクと不正ルートを解釈する', () => {
  expect(parseRoute('#/games/sample-1/detail')).toEqual({ page: 'detail', id: 'sample-1' })
  expect(parseRoute('')).toEqual({ page: 'home' })
  expect(parseRoute('#/history/deleted')).toEqual({ page: 'deleted' })
  expect(parseRoute('#/games/x/<script>')).toEqual({ page: 'notFound' })
})
