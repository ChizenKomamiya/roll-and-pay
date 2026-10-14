export type Route =
  | { page: 'login' | 'home' | 'participants' | 'history' | 'deleted' }
  | { page: 'dice' | 'payment' | 'result' | 'detail' | 'delete'; id: string }
  | { page: 'notFound' }

export function parseRoute(hash: string): Route {
  const path = hash.replace(/^#/, '') || '/home'
  if (path === '/history/deleted') return { page: 'deleted' }
  if (['/login', '/home', '/participants', '/history'].includes(path)) return { page: path.slice(1) as 'login' | 'home' | 'participants' | 'history' }
  const match = /^\/games\/([a-zA-Z0-9_-]+)\/(dice|payment|result|detail|delete)$/.exec(path)
  return match ? { page: match[2] as 'dice' | 'payment' | 'result' | 'detail' | 'delete', id: match[1] } : { page: 'notFound' }
}

export function navigate(path: string, replace = false) {
  if (replace) { history.replaceState(null, '', `#${path}`); window.dispatchEvent(new HashChangeEvent('hashchange')) }
  else if (location.hash !== `#${path}`) location.hash = path
}

export const gamePath = (id: string, page: 'dice' | 'payment' | 'result' | 'detail' | 'delete') => `/games/${id}/${page}`
