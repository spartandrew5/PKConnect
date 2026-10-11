import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// The backend is asked through the dev server, so the browser only ever talks to one origin.
// CONNECT_BACKEND points it somewhere else than the backend's default address.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5174,
    strictPort: true,
    proxy: {
      '/api': process.env.CONNECT_BACKEND ?? 'http://localhost:5000',
    },
  },
});