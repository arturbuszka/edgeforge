import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],

  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://127.0.0.1:5080', changeOrigin: true },

      // ws: true is required. Without it the SignalR WebSocket upgrade 404s while
      // every plain fetch to /api keeps working, which reads as a SignalR bug and
      // is not one.
      '/hub': { target: 'http://127.0.0.1:5080', changeOrigin: true, ws: true },
    },
  },

  // WHEP is deliberately NOT proxied: it goes straight to MediaMTX on 8889, at
  // the URL /api/config hands out. Routing SDP through Vite would add a hop for
  // nothing and break the ICE host matching the whole setup depends on.

  build: {
    // Straight into the API's wwwroot, so production is one process serving one
    // origin and CORS stops applying at all.
    outDir: '../src/EdgeForge.Api/wwwroot',
    emptyOutDir: true,
  },
})
