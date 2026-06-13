import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Vite config - the dashboard runs at http://localhost:3000 and proxies
// all /api/* calls to the Dashboard.Api at http://localhost:5000. This
// keeps the SPA free of CORS headaches during local dev.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 3000,
    proxy: {
      '/api': {
        target: process.env.KAMSORA_API_URL || 'http://localhost:5000',
        changeOrigin: true,
      },
    },
  },
  build: {
    sourcemap: true,
    chunkSizeWarningLimit: 1024,
  },
});
