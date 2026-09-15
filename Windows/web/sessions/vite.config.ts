import react from '@vitejs/plugin-react'
import { resolve } from 'node:path'
import { defineConfig } from 'vite'

// Built straight into the WPF project; the app serves it from https://sessions.zoomautoadmit/
// (index.html, the Sessions page) and https://dashboard.zoomautoadmit/ (dashboard.html).
export default defineConfig({
  base: './',
  plugins: [react()],
  build: {
    outDir: '../../src/ZoomAutoAdmit.WindowsUI/WebSessions',
    emptyOutDir: true,
    sourcemap: false,
    rollupOptions: { input: { index: resolve(import.meta.dirname, 'index.html'), dashboard: resolve(import.meta.dirname, 'dashboard.html'), roster: resolve(import.meta.dirname, 'roster.html') } },
  },
  server: { host: '127.0.0.1', port: 5190 },
})
