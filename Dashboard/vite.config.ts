import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

// The dashboard is served by the backend under /dashboard/ and talks to it on the same origin.
// In development, Vite forwards /api to the backend, so the browser still sees one origin and
// the session cookie works exactly as in production.
const backend = process.env.DASHBOARD_BACKEND_URL ?? 'http://127.0.0.1:8765'

export default defineConfig({
  base: '/dashboard/',
  plugins: [react(), tailwindcss()],
  server: {
    host: '127.0.0.1',
    port: 5173,
    proxy: { '/api': { target: backend, changeOrigin: false } },
  },
  build: { outDir: 'dist', sourcemap: false },
  test: {
    // Worker threads: the default child-process pool times out starting jsdom on this machine.
    pool: 'threads',
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    css: false,
  },
})
