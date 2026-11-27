// Notification read behavior in the bell, the Alerts page and across tabs (prompt 0067).
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import type { ReactNode } from 'react'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import { afterEach, beforeEach, expect, it, vi, type MockInstance } from 'vitest'
import { alertsApi, type Incident, type NotificationItem, type NotificationSummary } from '../../shared/alerts'
import { useLiveUpdates } from '../../shared/live'
import { AlertsPage } from './AlertsPage'
import { NotificationBell } from './NotificationCenter'

// A SignalR stand-in: every connection is one open tab of the same signed-in user.
const hub = vi.hoisted(() => ({ connections: [] as { handlers: Map<string, (...args: unknown[]) => void> }[] }))
vi.mock('@microsoft/signalr', () => {
  class Connection {
    handlers = new Map<string, (...args: unknown[]) => void>()
    constructor() { hub.connections.push(this) }
    on(name: string, handler: (...args: unknown[]) => void) { this.handlers.set(name, handler) }
    off(name: string) { this.handlers.delete(name) }
    start() { return Promise.resolve() }
    stop() { return Promise.resolve() }
    invoke() { return Promise.resolve() }
    onreconnecting() {}
    onreconnected() {}
    onclose() {}
  }
  class HubConnectionBuilder {
    withUrl() { return this }
    configureLogging() { return this }
    withAutomaticReconnect() { return this }
    build() { return new Connection() }
  }
  return { HubConnectionBuilder, HttpTransportType: { WebSockets: 1, LongPolling: 4 }, LogLevel: { None: 6 } }
})

const property = { id: 1, name: 'Countryside House', timeZone: 'Europe/Sofia' }
const at = '2026-11-27T13:20:00Z'
const incident: Incident = {
  id: 9, propertyId: 1, propertyName: 'Countryside House', title: 'Grid outage', correlationKey: 'grid.outage', status: 'active',
  severity: 'warning', originalSeverity: 'warning', maxSeverity: 'warning', startedAt: at, lastChangedAt: at, resolvedAt: null, resolution: null,
  acknowledgedAt: null, rules: [],
}
const notification = (id: number, title: string, severity: NotificationItem['severity'] = 'warning'): NotificationItem => ({
  id, propertyId: 1, propertyName: 'Countryside House', incidentId: 9, kind: 'activated', severity, title, body: 'Grid power is unavailable.',
  createdAt: at, inApp: true, readAt: null, suppressedReason: null, incidentStatus: 'active', incidentSeverity: severity, deliveries: [],
})

/** The server's per-user read state; marking read changes only the caller's recipient rows and notifies that user's tabs. */
const server = {
  user: 'owner',
  reads: new Map<string, Set<number>>(),
  items: [] as NotificationItem[],
  fail: false,
  incident: { ...incident },
}
const readBy = (user: string) => server.reads.get(user) ?? server.reads.set(user, new Set()).get(user)!
const view = (): NotificationItem[] => server.items.map(item => ({ ...item, readAt: readBy(server.user).has(item.id) ? at : null }))
const summary = (): NotificationSummary => {
  const unread = view().filter(item => !item.readAt)
  return { unread: unread.length, unreadCritical: unread.filter(item => item.severity === 'critical').length,
    unreadWarning: unread.filter(item => item.severity === 'warning').length, activeCritical: [] }
}

let markRead: MockInstance<typeof alertsApi.markRead>
let acknowledge: MockInstance<typeof alertsApi.acknowledge>
beforeEach(() => {
  server.user = 'owner'
  server.reads = new Map()
  server.items = [notification(5, 'Grid power lost'), notification(6, 'Grid outage escalated to Critical', 'critical')]
  server.fail = false
  server.incident = { ...incident }
  hub.connections.length = 0
  vi.spyOn(alertsApi, 'summary').mockImplementation(async () => summary())
  vi.spyOn(alertsApi, 'notifications').mockImplementation(async () => ({ next: null, items: view() }))
  markRead = vi.spyOn(alertsApi, 'markRead').mockImplementation(async body => {
    if (server.fail) throw new Error('Request failed (500)')
    const mine = readBy(server.user)
    const targets = body.all ? server.items.map(item => item.id) : body.ids ?? []
    const changed = targets.filter(id => !mine.has(id) && server.items.some(item => item.id === id))
    changed.forEach(id => mine.add(id))
    if (changed.length > 0) hub.connections.forEach(connection => connection.handlers.get('notificationsChanged')?.())
    return { changed: changed.length }
  })
  acknowledge = vi.spyOn(alertsApi, 'acknowledge').mockResolvedValue({ id: 9 })
  vi.spyOn(alertsApi, 'incidents').mockImplementation(async () => ({ canAcknowledge: true, incidents: [server.incident] }))
  vi.spyOn(alertsApi, 'incident').mockImplementation(async () => ({ incident: server.incident, timeline: [], canAcknowledge: true }))
})
afterEach(() => { cleanup(); vi.restoreAllMocks() })

function Where() {
  const location = useLocation()
  return <output aria-label="Location">{location.pathname}{location.search}</output>
}
function Tab({ children }: { children?: ReactNode }) {
  useLiveUpdates(property.id)
  return <><NotificationBell property={property} />{children}<Where /></>
}
function show(node: ReactNode = <Tab />, path = '/properties/1') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[path]}>
    <Routes><Route path="*" element={node} /></Routes></MemoryRouter></QueryClientProvider>)
}
const bell = (scope: ReturnType<typeof within> | typeof screen, unread: number) =>
  scope.findByRole('button', { name: new RegExp(`^Notifications, ${unread} unread`) })

it('opens the bell without marking anything read', async () => {
  show()
  fireEvent.click(await bell(screen, 2))
  const panel = await screen.findByRole('region', { name: 'Recent notifications' })
  expect(await within(panel).findByRole('link', { name: /Grid power lost.*Unread/ })).toBeInTheDocument()
  fireEvent.keyDown(panel, { key: 'Escape' })
  expect(screen.queryByRole('region', { name: 'Recent notifications' })).not.toBeInTheDocument()
  expect(await bell(screen, 2)).toBeInTheDocument()
  expect(markRead).not.toHaveBeenCalled()
})

it('marks one clicked notification read, opens its incident and updates the counter as soon as the server confirms', async () => {
  show()
  fireEvent.click(await bell(screen, 2))
  const link = await screen.findByRole('link', { name: /Grid power lost/ })
  expect(link.closest('li')).toHaveClass('unread')
  // Later reads stay pending, so the counter below can only come from the confirmed request.
  vi.mocked(alertsApi.summary).mockReturnValue(new Promise(() => undefined))
  vi.mocked(alertsApi.notifications).mockReturnValue(new Promise(() => undefined))
  fireEvent.click(link)
  fireEvent.click(link)
  expect(screen.getByRole('status', { name: 'Location' })).toHaveTextContent('/properties/1/alerts?incident=9')
  expect(await bell(screen, 1)).toHaveAccessibleName('Notifications, 1 unread, 1 critical')
  expect(markRead).toHaveBeenCalledTimes(1)
  expect(markRead).toHaveBeenCalledWith({ ids: [5] })
  fireEvent.click(await bell(screen, 1))
  const read = await screen.findByRole('link', { name: /Grid power lost/ })
  expect(read.closest('li')).toHaveClass('read')
  expect(read).not.toHaveAccessibleName(/Unread/)
  expect(screen.getByRole('link', { name: /escalated to Critical.*Unread/ }).closest('li')).toHaveClass('unread')
  // Reading is not acknowledging or resolving.
  expect(acknowledge).not.toHaveBeenCalled()
  expect(server.incident).toMatchObject({ acknowledgedAt: null, status: 'active', resolvedAt: null })
})

it('does not send a request for a notification that is already read', async () => {
  readBy('owner').add(5)
  show()
  fireEvent.click(await bell(screen, 1))
  fireEvent.click(await screen.findByRole('link', { name: /Grid power lost/ }))
  await screen.findByRole('button', { name: /^Notifications, 1 unread/ })
  expect(markRead).not.toHaveBeenCalled()
})

it('keeps a notification unread and says so when the server refuses the read', async () => {
  server.fail = true
  show()
  fireEvent.click(await bell(screen, 2))
  fireEvent.click(await screen.findByRole('link', { name: /Grid power lost/ }))
  expect(await screen.findByRole('alert')).toHaveTextContent('could not be marked as read. It is still unread.')
  expect(await bell(screen, 2)).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }))
  expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  fireEvent.click(await bell(screen, 2))
  expect((await screen.findByRole('link', { name: /Grid power lost/ })).closest('li')).toHaveClass('unread')
  // Mark all read fails the same way, without a false success.
  fireEvent.click(screen.getByRole('button', { name: 'Mark all read' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('Notifications could not be marked as read.')
  expect(await bell(screen, 2)).toBeInTheDocument()
  expect(readBy('owner').size).toBe(0)
})

it('marks everything read with Mark all read and keeps that after a reload', async () => {
  const first = show()
  fireEvent.click(await bell(screen, 2))
  fireEvent.click(await screen.findByRole('button', { name: 'Mark all read' }))
  expect(await bell(screen, 0)).toBeInTheDocument()
  expect(markRead).toHaveBeenCalledWith({ all: true, propertyId: undefined })
  first.unmount()
  show()
  fireEvent.click(await bell(screen, 0))
  for (const link of await screen.findAllByRole('link', { name: /Grid/ })) expect(link.closest('li')).toHaveClass('read')
})

it('keeps a single read after a reload and keeps read state separate for each user', async () => {
  const first = show()
  fireEvent.click(await bell(screen, 2))
  fireEvent.click(await screen.findByRole('link', { name: /Grid power lost/ }))
  await bell(screen, 1)
  first.unmount()
  const reloaded = show()
  expect(await bell(screen, 1)).toBeInTheDocument()
  reloaded.unmount()
  server.user = 'viewer'
  show()
  expect(await bell(screen, 2)).toBeInTheDocument()
})

it('updates another open tab of the same user through the live notificationsChanged event', async () => {
  const one = show()
  const two = show()
  const tabOne = within(one.container)
  const tabTwo = within(two.container)
  await bell(tabTwo, 2)
  await waitFor(() => expect(hub.connections).toHaveLength(2))
  fireEvent.click(await bell(tabOne, 2))
  fireEvent.click(await tabOne.findByRole('link', { name: /Grid power lost/ }))
  expect(await bell(tabOne, 1)).toBeInTheDocument()
  expect(await bell(tabTwo, 1)).toBeInTheDocument()
  expect(markRead).toHaveBeenCalledTimes(1)
})

it('marks the named notification read once when an incident link carries it, and nothing for a plain incident link', async () => {
  show(<Tab><AlertsPage property={property} properties={[property]} /></Tab>, '/properties/1/alerts?incident=9&notification=6')
  await waitFor(() => expect(markRead).toHaveBeenCalledWith({ ids: [6] }))
  expect(await bell(screen, 1)).toHaveAccessibleName('Notifications, 1 unread')
  await waitFor(() => expect(screen.getByRole('status', { name: 'Location' })).toHaveTextContent('/properties/1/alerts?incident=9'))
  expect(screen.getByRole('status', { name: 'Location' })).not.toHaveTextContent('notification')
  expect(await screen.findByRole('button', { name: 'Acknowledge' })).toBeInTheDocument()
  expect(markRead).toHaveBeenCalledTimes(1)
  expect(acknowledge).not.toHaveBeenCalled()
  cleanup()
  markRead.mockClear()
  show(<Tab><AlertsPage property={property} properties={[property]} /></Tab>, '/properties/1/alerts?incident=9')
  expect(await screen.findByRole('button', { name: 'Acknowledge' })).toBeInTheDocument()
  expect(await bell(screen, 1)).toBeInTheDocument()
  expect(markRead).not.toHaveBeenCalled()
})

it('opens an incident from notification history and marks only that notification read', async () => {
  show(<Tab><AlertsPage property={property} properties={[property]} /></Tab>, '/properties/1/alerts')
  fireEvent.click(screen.getByRole('tab', { name: 'Notification history' }))
  const history = await screen.findByRole('link', { name: 'Grid outage escalated to Critical' })
  expect(history.closest('li')).toHaveClass('unread')
  // Repeated clicks while the request is in flight send it once.
  let release: (value: { changed: number }) => void = () => undefined
  const original = markRead.getMockImplementation()!
  markRead.mockImplementationOnce(body => new Promise<{ changed: number }>(resolve => { release = resolve as typeof release }).then(() => original(body)))
  const button = screen.getByRole('button', { name: 'Mark “Grid power lost” read' })
  fireEvent.click(button)
  fireEvent.click(button)
  fireEvent.click(button)
  release({ changed: 0 })
  await waitFor(() => expect(screen.queryByRole('button', { name: 'Mark “Grid power lost” read' })).not.toBeInTheDocument())
  expect(markRead).toHaveBeenCalledTimes(1)
  fireEvent.click(history)
  await waitFor(() => expect(markRead).toHaveBeenCalledWith({ ids: [6] }))
  expect(await screen.findByRole('tab', { name: 'Incidents', selected: true })).toBeInTheDocument()
  expect(await bell(screen, 0)).toBeInTheDocument()
  expect(markRead).toHaveBeenCalledTimes(2)
  expect(acknowledge).not.toHaveBeenCalled()
})

it('reaches bell notifications with the keyboard as ordinary links', async () => {
  show()
  const trigger = await bell(screen, 2)
  expect(trigger).toHaveAttribute('aria-expanded', 'false')
  fireEvent.click(trigger)
  expect(trigger).toHaveAttribute('aria-expanded', 'true')
  const links = await screen.findAllByRole('link', { name: /Grid/ })
  for (const link of links) {
    expect(link).toHaveAttribute('href', '/properties/1/alerts?incident=9')
    link.focus()
    expect(link).toHaveFocus()
  }
})
