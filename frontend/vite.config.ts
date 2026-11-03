import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// ALSHIFA_API lets a test run reach its own API and database without disturbing a running copy on 5080.
const apiTarget = process.env.ALSHIFA_API ?? 'http://localhost:5080'

export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': apiTarget,
      '/hubs': { target: apiTarget, ws: true },
    },
  },
})
