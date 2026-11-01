import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    include: ['test/**/*.test.ts'],
    // Timings run 20x faster than real; one file at a time keeps timers from drifting under CPU load.
    fileParallelism: false,
    testTimeout: 20000,
    hookTimeout: 20000,
  },
});
