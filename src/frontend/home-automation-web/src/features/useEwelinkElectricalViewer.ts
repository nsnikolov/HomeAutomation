import { useEffect } from 'react'
import { api } from '../shared/api'

export function useEwelinkElectricalViewer(property: number, enabled: boolean) {
  useEffect(() => {
    if (!enabled) return
    const tab = crypto.randomUUID()
    let disposed = false
    let pageHidden = false
    let running = false
    let registered = false
    let send: Awaited<ReturnType<typeof api.prepareEwelinkElectricalViewer>> | undefined
    const visible = () => !disposed && !pageHidden && document.visibilityState === 'visible'

    async function update() {
      const active = visible()
      if (running || (!active && !registered)) return
      running = true
      try {
        send ??= await api.prepareEwelinkElectricalViewer(property, tab)
        if (active && !visible()) return
        registered = active
        await send(active)
      } catch {
        // The server expires an unattended viewer even if a heartbeat or final stop is lost.
      } finally {
        running = false
        if (active !== visible()) void update()
      }
    }

    const sync = () => { void update() }
    const hide = () => { pageHidden = true; sync() }
    const show = () => { pageHidden = false; sync() }
    document.addEventListener('visibilitychange', sync)
    window.addEventListener('pagehide', hide)
    window.addEventListener('pageshow', show)
    const heartbeat = window.setInterval(sync, 10_000)
    sync()
    return () => {
      disposed = true
      window.clearInterval(heartbeat)
      document.removeEventListener('visibilitychange', sync)
      window.removeEventListener('pagehide', hide)
      window.removeEventListener('pageshow', show)
      sync()
    }
  }, [property, enabled])
}
