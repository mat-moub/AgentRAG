import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig(({ mode }) => ({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    proxy: {
      '/api': loadEnv(mode, process.cwd(), '').API_PROXY_TARGET || 'http://127.0.0.1:5096',
    },
  },
}))
