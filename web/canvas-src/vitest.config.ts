import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    environment: 'jsdom',
    include: ['tests/*.test.tsx', 'tests/*.test.ts'],
    setupFiles: ['tests/sdk-test-setup.ts'],
    testTimeout: 15000,
    pool: 'forks',
    maxWorkers: 1,
  },
});
