const { defineConfig, devices } = require('@playwright/test');

module.exports = defineConfig({
  testDir: './mock/tests',
  timeout: 30_000,
  expect: { timeout: 10_000 },
  use: {
    baseURL: 'http://localhost:8765',
    ...devices['Desktop Chrome'],
    viewport: { width: 1440, height: 900 },
  },
});
