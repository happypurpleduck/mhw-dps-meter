import { defineConfig } from 'vitest/config'
import solid from '@solidjs/vite-plugin'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [solid(), tailwindcss()],
  // Relative base so the built site works from any sub-path (GitHub Pages, a local folder).
  base: './',
  build: { target: 'esnext' },
  server: { port: 5173, open: false },
  test: {
    // jsdom makes Vitest resolve Solid's browser build (the node build is the SSR runtime).
    environment: 'jsdom',
    include: ['tests/**/*.test.ts'],
  },
})
