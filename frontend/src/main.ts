import { startApp } from './app'
import './styles/base.css'
import './styles/components.css'

const root = document.querySelector<HTMLDivElement>('#app')
if (!root) throw new Error('起動先の #app が見つかりません。')
const dispose = startApp(root)
if (import.meta.hot) import.meta.hot.dispose(dispose)
