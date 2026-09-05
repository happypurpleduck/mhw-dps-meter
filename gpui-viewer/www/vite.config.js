import { defineConfig } from 'vite'
import wasm from 'vite-plugin-wasm'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))

export default defineConfig({
  plugins: [
    wasm(),
    {
      // Serve ../sample-logs at /sample-logs during dev (copied into dist by the build script).
      name: 'serve-sample-logs',
      configureServer(server) {
        server.middlewares.use('/sample-logs', async (req, res, next) => {
          const fs = await import('node:fs')
          const file = path.join(here, '..', 'sample-logs', decodeURIComponent((req.url || '/').split('?')[0]))
          if (fs.existsSync(file) && fs.statSync(file).isFile()) {
            res.setHeader('Content-Type', 'application/json')
            fs.createReadStream(file).pipe(res)
          } else next()
        })
      },
    },
  ],
  base: './',
  build: {
    target: 'esnext',
    rollupOptions: {
      onwarn(warning, warn) {
        if (warning.code === 'EVAL' && warning.id?.includes('/src/wasm/')) return
        warn(warning)
      },
    },
  },
  server: {
    port: 3000,
    fs: { allow: ['..'] },
    headers: {
      'Cross-Origin-Embedder-Policy': 'require-corp',
      'Cross-Origin-Opener-Policy': 'same-origin',
    },
  },
  optimizeDeps: { exclude: ['./src/wasm'] },
})
