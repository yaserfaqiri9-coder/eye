import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './e2e',
  workers: 1,
  use: {
    // The tests need the dev server (they load /src modules). start.bat serves the built app on 5173 instead.
    baseURL: process.env.ALSHIFA_E2E_BASE ?? 'http://localhost:5173',
    channel: 'msedge',
    screenshot: 'only-on-failure',
  },
})
