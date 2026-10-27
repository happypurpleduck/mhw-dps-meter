import { defineConfig } from 'vitest/config'
import solid from '@solidjs/vite-plugin'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [solid(), tailwindcss()],
  // Tests run in Node without a DOM, but must use Solid's reactive browser
  // runtime rather than the inert server signal implementation.
  ssr: {
    noExternal: ['solid-js', '@solidjs/web'],
    resolve: { conditions: ['browser'] },
  },
  // Relative base so the built site works from any sub-path (GitHub Pages, a local folder).
  base: './',
  build: { target: 'esnext' },
  server: { port: 5173, open: false },
  test: {
    // Pure unit tests need no DOM; SSR resolution above selects reactive Solid.
    environment: 'node',
    include: ['tests/**/*.test.ts'],
  },
})
