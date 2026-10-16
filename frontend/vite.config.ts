import { defineConfig, loadEnv } from 'vite'
import { readConfig } from './src/config.ts'

export default defineConfig(({ mode }) => {
  readConfig(loadEnv(mode, '.', 'VITE_'))
  return { build: { rolldownOptions: { input: { main: 'index.html', redirect: 'redirect.html' } } } }
})
