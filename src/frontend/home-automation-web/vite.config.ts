import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { defineConfig } from 'vitest/config'

const localHttpsHost = 'ha-dev.britishlab.org:5443'

export default defineConfig(({ command, mode }) => {
  const localHttps = command === 'serve' && mode === 'local-https'
  const proxyTarget = localHttps ? 'http://127.0.0.1:5080' : 'http://localhost:5080'
  const headers = localHttps ? { 'x-forwarded-proto': 'https' } : undefined
  let https
  if (localHttps) {
    const localAppData = process.env.LOCALAPPDATA
    if (!localAppData) throw new Error('LOCALAPPDATA is required for the local HTTPS certificate.')
    const certificateDirectory = join(localAppData, 'HomeAutomation', 'tls')
    try {
      https = {
        cert: readFileSync(join(certificateDirectory, 'ha-dev.britishlab.org.pem')),
        key: readFileSync(join(certificateDirectory, 'ha-dev.britishlab.org-key.pem')),
      }
    } catch {
      throw new Error('Generate the local HTTPS certificate and key before starting dev:https. See the Windows setup guide.')
    }
  }

  return {
    plugins: [react(), tailwindcss(), {
      name: 'home-automation-local-https-host',
      configureServer(server) {
        if (!localHttps) return
        // Vite skips its built-in allowedHosts check for HTTPS.
        server.middlewares.use((request, response, next) => {
          const authority = request.headers[':authority'] ?? request.headers.host
          if (typeof authority === 'string' && authority.toLowerCase() === localHttpsHost) {
            request.headers.host = authority
            next()
            return
          }
          response.writeHead(421).end()
        })
      },
    }],
    build: {
      rolldownOptions: {
        output: { codeSplitting: { groups: [{ name: 'charts', test: /[\\/]node_modules[\\/]recharts[\\/]/ }] } },
      },
    },
    server: {
      host: localHttps ? '127.0.0.1' : 'localhost',
      port: localHttps ? 5443 : 5173,
      strictPort: true,
      https,
      cors: localHttps ? false : undefined,
      proxy: {
        '/api': { target: proxyTarget, headers },
        '/health': { target: proxyTarget, headers },
        '/hubs': { target: proxyTarget, headers, ws: true },
      },
    },
    test: { environment: 'jsdom', setupFiles: ['./src/test-setup.ts'] },
  }
})
