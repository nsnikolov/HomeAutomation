import { useEffect, useState } from 'react'
import type { DashboardWidget } from '../shared/types'

export type WidgetView = { mode: DashboardWidget['mode']; expanded: boolean }
export const defaultWidgetView: WidgetView = { mode: 'compact', expanded: true }

async function storageKey(email: string, propertyId: number) {
  const account = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(email))
  const scope = Array.from(new Uint8Array(account), byte => byte.toString(16).padStart(2, '0')).join('')
  return `dashboard-widget-views:v1:${scope}:${propertyId}`
}

function readViews(key: string): Map<string, WidgetView> {
  try {
    const saved: unknown = JSON.parse(localStorage.getItem(key) ?? 'null')
    if (!saved || typeof saved !== 'object' || Array.isArray(saved)) return new Map()
    const views = new Map<string, WidgetView>()
    for (const [id, view] of Object.entries(saved as Record<string, unknown>)) {
      if (!view || typeof view !== 'object' || !('mode' in view) || !('expanded' in view)) continue
      const { mode, expanded } = view
      if ((mode === 'compact' || mode === 'detailed') && typeof expanded === 'boolean') views.set(id, { mode, expanded })
    }
    return views
  } catch {
    return new Map()
  }
}

export function useDashboardWidgetViews(email: string, propertyId: number, confirmedWidgets?: DashboardWidget[]) {
  const [key, setKey] = useState<string | null>()
  const [views, setViews] = useState(() => new Map<string, WidgetView>())

  useEffect(() => {
    let active = true
    void storageKey(email, propertyId).then(key => {
      if (!active) return
      setViews(readViews(key))
      setKey(key)
    }).catch(() => { if (active) setKey(null) })
    return () => { active = false }
  }, [email, propertyId])

  useEffect(() => {
    if (!confirmedWidgets || key === undefined) return
    const ids = new Set(confirmedWidgets.map(widget => widget.id))
    // Save the pruned map with the next actual user change.
    setViews(current => {
      const live = new Map([...current].filter(([id]) => ids.has(id)))
      return live.size === current.size ? current : live
    })
  }, [confirmedWidgets, key])

  function update(ids: string[], change: Partial<WidgetView>) {
    const next = new Map(views)
    let changed = false
    for (const id of ids) {
      const current = next.get(id) ?? defaultWidgetView
      const view = { ...current, ...change }
      if (view.mode === current.mode && view.expanded === current.expanded) continue
      next.set(id, view)
      changed = true
    }
    if (!changed) return
    setViews(next)
    if (key) {
      try { localStorage.setItem(key, JSON.stringify(Object.fromEntries(next))) }
      catch { /* Continue using the in-memory preferences. */ }
    }
  }

  return { ready: key !== undefined, views, update }
}
