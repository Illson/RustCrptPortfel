import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './tests',
  timeout: 35_000,
  workers: 1,
  use: {
    baseURL: process.env.PORTFEL_E2E_URL ?? 'http://127.0.0.1:5080',
    channel: process.env.PORTFEL_E2E_CHANNEL,
    trace: 'retain-on-failure',
    colorScheme: 'light',
  },
})
