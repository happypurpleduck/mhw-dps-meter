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
    // These unit tests need no DOM. The Solid Vite plugin supplies browser
    // export conditions in Vitest; avoid an undeclared jsdom dependency.
    environment: 'node',
    include: ['tests/**/*.test.ts'],
  },
})
