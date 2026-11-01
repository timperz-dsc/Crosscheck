import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// During development (npm run dev) API calls go to a running bridge on 127.0.0.1:8790.
const bridge = process.env.CROSSCHECK_BRIDGE ?? 'http://127.0.0.1:8790';

export default defineConfig({
  plugins: [react()],
  server: {
    host: '127.0.0.1',
    proxy: { '/api': { target: bridge, ws: true, changeOrigin: true } },
  },
  build: { outDir: 'dist', emptyOutDir: true, assetsInlineLimit: 0 },
  test: {
    environment: 'jsdom',
    setupFiles: ['src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
  },
});
