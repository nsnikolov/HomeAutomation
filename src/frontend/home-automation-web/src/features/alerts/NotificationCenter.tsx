import { useCallback, useEffect, useId, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query'
import { Bell, X } from 'lucide-react'
import { Link } from 'react-router'
import { alertsApi, refreshNotifications, type BrowserNotification, type NotificationItem, type NotificationPage, type NotificationSummary } from '../../shared/alerts'
import type { Property } from '../../shared/types'
import { Button } from '../../shared/ui'
import { formatMoment } from '../../shared/utils'
import { SeverityBadge } from './AlertBits'

/** Event name the live connection uses to hand a browser notification to this page. */
export const browserNotificationEvent = 'homeautomation:browser-notification'

export const summaryKey = ['notifications', 'summary'] as const

export function useNotificationSummary() {
  return useQuery({ queryKey: summaryKey, queryFn: ({ signal }) => alertsApi.summary(signal), refetchInterval: 60_000, retry: false })
}

/** The incident a notification belongs to; `notification` lets the Alerts page mark exactly that one read when opened directly. */
export const notificationLink = (item: Pick<NotificationItem, 'id' | 'propertyId' | 'incidentId'>, withId = false) =>
  `/properties/${item.propertyId}/alerts?incident=${item.incidentId}${withId ? `&notification=${item.id}` : ''}`

// Requests in flight in this tab, so a double click, the bell and the Alerts page never send the same read twice.
const pendingReads = new Set<number>()

type ReadTarget = number | Pick<NotificationItem, 'id' | 'severity' | 'inApp' | 'readAt'>

/** Applies a persisted read to cached lists and the bell summary, so counters change as soon as the server confirms. */
function applyRead(client: QueryClient, ids: number[] | 'all', readAt: string, propertyId?: number) {
  const mark = (item: NotificationItem) => !item.readAt && (ids === 'all' ? propertyId === undefined || item.propertyId === propertyId : ids.includes(item.id))
    ? { ...item, readAt } : item
  client.setQueriesData<NotificationPage | NotificationPage[]>({ queryKey: ['notifications', 'list'] }, data =>
    !data ? data : Array.isArray(data) ? data.map(page => ({ ...page, items: page.items.map(mark) })) : { ...data, items: data.items.map(mark) })
}

/**
 * Marks notifications read for the signed-in user. Read state is only shown after the server confirms it; a failure leaves the
 * notification unread and is reported. Reading never acknowledges or resolves an incident. Other tabs follow through the
 * `notificationsChanged` live event the server sends to this user's connections.
 */
export function useNotificationReads() {
  const client = useQueryClient()
  const [error, setError] = useState<string | null>(null)
  const markOne = useCallback(async (target: ReadTarget) => {
    const id = typeof target === 'number' ? target : target.id
    if ((typeof target !== 'number' && target.readAt) || pendingReads.has(id)) return
    pendingReads.add(id)
    setError(null)
    try {
      const { changed } = await alertsApi.markRead({ ids: [id] })
      await refreshNotifications(client, () => {
        if (changed === 0) return
        applyRead(client, [id], new Date().toISOString())
        if (typeof target !== 'number' && target.inApp)
          client.setQueryData<NotificationSummary>(summaryKey, summary => summary && {
            ...summary, unread: Math.max(0, summary.unread - 1),
            unreadCritical: Math.max(0, summary.unreadCritical - (target.severity === 'critical' ? 1 : 0)),
            unreadWarning: Math.max(0, summary.unreadWarning - (target.severity === 'warning' ? 1 : 0)),
          })
      })
    } catch {
      setError('The notification could not be marked as read. It is still unread.')
    } finally {
      pendingReads.delete(id)
    }
  }, [client])
  const all = useMutation({
    mutationFn: (propertyId?: number) => alertsApi.markRead({ all: true, propertyId }),
    onMutate: () => setError(null),
    onSuccess: (_, propertyId) => refreshNotifications(client, () => {
      applyRead(client, 'all', new Date().toISOString(), propertyId)
      if (propertyId === undefined)
        client.setQueryData<NotificationSummary>(summaryKey, summary => summary && { ...summary, unread: 0, unreadCritical: 0, unreadWarning: 0 })
    }),
    onError: () => setError('Notifications could not be marked as read. They are still unread.'),
  })
  return { markOne, markAll: (propertyId?: number) => all.mutate(propertyId), markingAll: all.isPending, error, clearError: () => setError(null) }
}

/** The header bell: unread in-app notifications across the user's properties, with Critical and Warning counts. Opening it reads nothing. */
export function NotificationBell({ property }: { property: Property }) {
  const summary = useNotificationSummary()
  const [open, setOpen] = useState(false)
  const panelId = useId()
  const root = useRef<HTMLDivElement>(null)
  const recent = useQuery({
    queryKey: ['notifications', 'list', { inApp: true, limit: 8 }],
    queryFn: ({ signal }) => alertsApi.notifications({ inApp: true, limit: 8 }, signal), enabled: open, retry: false,
  })
  const reads = useNotificationReads()
  useEffect(() => {
    if (!open) return
    const outside = (event: MouseEvent) => { if (!root.current?.contains(event.target as Node)) setOpen(false) }
    document.addEventListener('mousedown', outside)
    return () => document.removeEventListener('mousedown', outside)
  }, [open])
  if (summary.isError) return null
  const data = summary.data
  const unread = data?.unread ?? 0
  const label = `Notifications, ${unread} unread${data?.unreadCritical ? `, ${data.unreadCritical} critical` : ''}`
  return <div className="notification-bell" ref={root} onKeyDown={event => { if (event.key === 'Escape') setOpen(false) }}>
    <button type="button" className="bell-trigger" aria-label={label} aria-expanded={open} aria-controls={panelId} onClick={() => setOpen(!open)}>
      <Bell size={18} aria-hidden />
      {unread > 0 && <span className={`bell-count${data?.unreadCritical ? ' bell-critical' : data?.unreadWarning ? ' bell-warning' : ''}`}
        aria-hidden>{unread > 99 ? '99+' : unread}</span>}
    </button>
    {open && <div id={panelId} className="bell-panel" role="region" aria-label="Recent notifications">
      <div className="bell-heading"><strong>Notifications</strong>
        <Button variant="ghost" disabled={unread === 0 || reads.markingAll} onClick={() => reads.markAll()}>Mark all read</Button></div>
      {recent.isPending && <p role="status" className="muted">Loading…</p>}
      {recent.isError && <p role="alert" className="form-error">Notifications could not be loaded.</p>}
      {recent.data?.items.length === 0 && <p className="muted">No notifications yet.</p>}
      <ul className="bell-list">{recent.data?.items.map(item => <li key={item.id} className={item.readAt ? 'read' : 'unread'}>
        <Link className="bell-item" to={notificationLink(item)} onClick={() => { setOpen(false); void reads.markOne(item) }}>
          <SeverityBadge severity={item.severity} />
          <span className="bell-title">{item.title}</span>
          <small className="muted">{item.propertyName} · {formatMoment(item.createdAt, property.timeZone)}
            {item.readAt ? '' : <> · <span className="unread-label">Unread</span></>}</small>
        </Link>
      </li>)}</ul>
      <Link className="text-link" to={`/properties/${property.id}/alerts`} onClick={() => setOpen(false)}>Open alerts and history</Link>
    </div>}
    {reads.error && <p className="bell-error" role="alert">{reads.error}
      <button type="button" aria-label="Dismiss" onClick={reads.clearError}><X size={14} aria-hidden /></button></p>}
  </div>
}

/** Active Critical incidents stay visible until they resolve, whether or not their notifications were read. */
export function CriticalBanner({ property }: { property: Property }) {
  const summary = useNotificationSummary()
  const incidents = summary.data?.activeCritical ?? []
  if (incidents.length === 0) return null
  return <div className="critical-banner" role="alert">
    <SeverityBadge severity="critical" />
    <ul>{incidents.map(incident => <li key={incident.id}>
      <strong>{incident.title}</strong> · {incident.propertyName} · since {formatMoment(incident.startedAt, property.timeZone)}
      {incident.acknowledgedAt ? ' · Acknowledged' : ''}{' '}
      <Link className="text-link" to={`/properties/${incident.propertyId}/alerts?incident=${incident.id}`}>View incident</Link>
    </li>)}</ul>
  </div>
}

/**
 * Shows browser notifications handed over by the live connection. A system notification appears only while this tab is in the
 * background and permission is granted; a visible tab already shows the bell and banner. Each outcome is reported to the server.
 */
export function useBrowserNotifications() {
  useEffect(() => {
    const handle = (event: Event) => {
      const message = (event as CustomEvent<BrowserNotification>).detail
      if (message.sound) playAlertSound()
      const report = (outcome: 'shown' | 'notShown', reason?: 'permission' | 'unsupported' | 'visible' | 'error') =>
        void alertsApi.reportDelivery(message.notificationId, outcome, reason).catch(() => undefined)
      if (!('Notification' in window)) { report('notShown', 'unsupported'); return }
      if (document.visibilityState === 'visible') { report('notShown', 'visible'); return }
      if (Notification.permission !== 'granted') { report('notShown', 'permission'); return }
      try {
        const shown = new Notification(message.title, {
          body: message.body, tag: `homeautomation-${message.notificationId}`, requireInteraction: message.severity === 'critical',
        })
        shown.onclick = () => {
          window.focus()
          window.location.assign(notificationLink({ id: message.notificationId, propertyId: message.propertyId, incidentId: message.incidentId }, true))
        }
        report('shown')
      } catch {
        report('notShown', 'error')
      }
    }
    window.addEventListener(browserNotificationEvent, handle)
    return () => window.removeEventListener(browserNotificationEvent, handle)
  }, [])
}

/** A short two-tone chime for Critical notifications, when the user turned sound on. Browsers may block audio until interaction. */
export function playAlertSound() {
  try {
    const AudioContextType = window.AudioContext ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext
    if (!AudioContextType) return
    const context = new AudioContextType()
    const gain = context.createGain()
    gain.gain.value = 0.08
    gain.connect(context.destination)
    ;[880, 660].forEach((frequency, index) => {
      const tone = context.createOscillator()
      tone.frequency.value = frequency
      tone.connect(gain)
      tone.start(context.currentTime + index * 0.22)
      tone.stop(context.currentTime + index * 0.22 + 0.18)
    })
    setTimeout(() => void context.close().catch(() => undefined), 800)
  } catch {
    // Audio is optional; the notification itself is already persisted and visible in the app.
  }
}
