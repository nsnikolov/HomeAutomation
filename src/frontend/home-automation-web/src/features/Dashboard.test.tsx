import { readFileSync } from 'node:fs'
import { webcrypto } from 'node:crypto'
import { useState, type ComponentProps } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { Link, MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError } from '../shared/api'
import type { DashboardGroup, DashboardReading, DashboardSnapshot, DashboardSource } from '../shared/types'
import { Dashboard } from './Dashboard'
import { DashboardGroups, dashboardFreshness, useDashboardTime } from './DashboardWidgets'
import { defaultWidgetView, type WidgetView } from './dashboardWidgetViews'

const email = 'viewer@example.test'
const property = { id: 3, name: 'Test property', timeZone: 'Europe/Sofia' }
const now = Date.parse('2026-11-24T08:00:00Z')
const reading = (number: number, age: number): DashboardReading => ({
  number, flag: null, code: null, observedAt: new Date(now - age).toISOString(), provenance: 'serverReceived',
})
const source: DashboardSource = {
  key: 'ewelink:fixture:ActivePower', provider: 'ewelink', deviceId: 'fixture', deviceName: 'Heater', metric: 'activePower',
  unit: 'W', channel: null, route: '/properties/3/ewelink', reading: reading(0, 5 * 60000),
}
const groups: DashboardGroup[] = [{
  id: 'electrical', title: 'Electrical', hidden: false, collapsed: true,
  widgets: Array.from({ length: 5 }, (_, index) => ({
    id: `widget-${index}`, title: `Widget ${index + 1}`, mode: 'compact', navigationSource: source.key,
    values: Array.from({ length: 5 }, (_, value) => ({
      id: `reading-${value}`, sourceKey: value === 1 ? 'removed' : source.key, label: `Value ${value + 1}`,
    })),
  })),
}]

beforeEach(() => {
  vi.stubGlobal('crypto', webcrypto)
  localStorage.clear()
})

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

function TestDashboardGroups(props: Omit<ComponentProps<typeof DashboardGroups>, 'views' | 'onViewChange'>) {
  const [views, setViews] = useState(new Map<string, WidgetView>())
  return <DashboardGroups {...props} views={views} onViewChange={(id, change) =>
    setViews(current => new Map(current).set(id, { ...(current.get(id) ?? defaultWidgetView), ...change }))} />
}

function mockGridPositions(columns: () => number) {
  vi.spyOn(HTMLElement.prototype, 'getClientRects').mockReturnValue({ length: 1 } as DOMRectList)
  vi.spyOn(HTMLElement.prototype, 'offsetTop', 'get').mockImplementation(function (this: HTMLElement) {
    return Math.floor(Array.from(this.parentElement?.children ?? []).indexOf(this) / columns()) * 50
  })
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(44)
}

describe('configurable dashboard', () => {
  it('uses the exact color boundaries and treats zero and textual readings as valid', () => {
    expect(dashboardFreshness(reading(0, 5 * 60000 - 1), now).tier).toBe('fresh')
    expect(dashboardFreshness(reading(0, 5 * 60000), now).tier).toBe('aging')
    expect(dashboardFreshness({ ...reading(0, 60 * 60000 - 1), number: null, flag: false }, now).tier).toBe('aging')
    expect(dashboardFreshness({ ...reading(0, 60 * 60000), number: null, code: 'heating' }, now).tier).toBe('old')
    expect(dashboardFreshness(null, now).tier).toBe('missing')
    expect(dashboardFreshness(reading(Infinity, 1), now).tier).toBe('missing')
    expect(dashboardFreshness(reading(2, -1), now).tier).toBe('missing')
    for (const observedAt of ['2026-11-24T07:00:00', '2026-02-30T07:00:00Z', 'invalid']) {
      expect(dashboardFreshness({ ...reading(0, 0), observedAt }, now).tier).toBe('missing')
    }
    expect(dashboardFreshness({ ...reading(0, 0), observedAt: '2026-11-24T09:00:00+02:00' }, now).tier).toBe('old')
  })

  it('advances freshness without telemetry or a refetch', () => {
    vi.useFakeTimers()
    vi.setSystemTime(now)
    const timed = { ...source, reading: reading(0, 5 * 60000 - 1) }
    const sources = [timed]
    function Clock() { return <output>{dashboardFreshness(timed.reading, useDashboardTime(sources)).tier}</output> }
    render(<Clock />)
    expect(screen.getByText('fresh')).toBeInTheDocument()
    act(() => vi.advanceTimersByTime(1))
    expect(screen.getByText('aging')).toBeInTheDocument()
    act(() => vi.advanceTimersByTime(55 * 60000))
    expect(screen.getByText('old')).toBeInTheDocument()
  })

  it('collapses by measured rows and retains independent expansion as new data and widths arrive', () => {
    let columns = 2
    const observers = new Set<() => void>()
    vi.stubGlobal('ResizeObserver', class {
      constructor(private readonly callback: () => void) { observers.add(callback) }
      observe() {}
      disconnect() { observers.delete(this.callback) }
    })
    mockGridPositions(() => columns)
    const view = (sample: DashboardSource) => <MemoryRouter>
      <TestDashboardGroups groups={groups} sources={new Map([[sample.key, sample]])} property={property} now={now} />
    </MemoryRouter>
    const { rerender } = render(view(source))
    expect(screen.getByRole('heading', { name: 'Widget 5' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Expand Electrical' })).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Electrical' })).toBeInTheDocument()
    const first = screen.getByRole('heading', { name: 'Widget 1' }).closest('article')!
    expect(within(first).getAllByText('0 W')[0]).toHaveClass('monitor-age-aging')
    expect(within(first).getByText('—')).toHaveAttribute('title', expect.stringContaining('no longer available'))
    const widgetControl = within(first).getByRole('button', { name: 'Collapse Widget 1' })
    expect(widgetControl).toHaveAttribute('aria-expanded', 'true')
    fireEvent.click(widgetControl)
    expect(within(first).getByRole('button', { name: 'Show all Widget 1' })).toHaveAttribute('aria-expanded', 'false')
    expect(screen.getByRole('heading', { name: 'Widget 5' })).toBeInTheDocument()
    rerender(view({ ...source, reading: reading(42, 100) }))
    expect(within(first).getAllByText('42 W')[0]).toHaveClass('monitor-age-fresh')
    expect(within(first).getByRole('button', { name: 'Show all Widget 1' })).toBeInTheDocument()
    act(() => { columns = 3; observers.forEach(callback => callback()) })
    expect(within(first).queryByRole('button', { name: 'Show all Widget 1' })).not.toBeInTheDocument()
    act(() => { columns = 1; observers.forEach(callback => callback()) })
    expect(within(first).getByRole('button', { name: 'Show all Widget 1' })).toBeInTheDocument()
  })

  it('routes mixed-provider measurements separately and leaves missing or unsafe sources inert', () => {
    const tuya = { ...source, key: 'tuya:sensor:temp', provider: 'tuya', deviceName: 'Thermometer', route: '/properties/3/tuya' } satisfies DashboardSource
    const unsafe = { ...tuya, key: 'unsafe', route: '/properties/4/tuya' }
    const target = { ...groups[0], widgets: [{ ...groups[0].widgets[0], values: [
      { id: 'relay', label: 'Relay', sourceKey: source.key },
      { id: 'temp', label: 'Thermometer reading', sourceKey: tuya.key },
      { id: 'missing', label: 'Deleted source', sourceKey: 'removed' },
      { id: 'unsafe', label: 'Other property', sourceKey: unsafe.key },
    ] }] }
    render(<MemoryRouter initialEntries={['/']}>
      <Routes>
        <Route path="/" element={<TestDashboardGroups groups={[target]} sources={new Map([source, tuya, unsafe].map(item => [item.key, item]))}
          property={property} now={now} />} />
        <Route path={source.route} element={<h1>Provider detail</h1>} />
      </Routes>
    </MemoryRouter>)
    expect(screen.queryByRole('button', { name: /on|off|switch/i })).not.toBeInTheDocument()
    const view = screen.getByRole('button', { name: 'Show detailed view for Widget 1' })
    expect(view).toHaveAttribute('title', 'Show detailed view for Widget 1')
    expect(view.querySelector('.lucide-rows-3')).toHaveAttribute('width', '16')
    fireEvent.click(view)
    const compactView = screen.getByRole('button', { name: 'Show compact view for Widget 1' })
    expect(compactView).toBe(view)
    expect(compactView).toHaveAttribute('title', 'Show compact view for Widget 1')
    expect(compactView.querySelector('.lucide-rows-2')).toHaveAttribute('width', '16')
    expect(screen.queryByRole('heading', { name: 'Provider detail' })).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Open Thermometer for Thermometer reading/ })).toHaveAttribute('href', tuya.route)
    expect(screen.queryByRole('link', { name: /Deleted source|Other property/ })).not.toBeInTheDocument()
    expect(screen.getByText('—')).toBeInTheDocument()
    const navigation = screen.getByRole('link', { name: /Open Heater for Relay/ })
    expect(navigation).toHaveAttribute('href', source.route)
    fireEvent.click(navigation)
    expect(screen.getByRole('heading', { name: 'Provider detail' })).toBeInTheDocument()
  })

  it('keeps an empty dashboard read-only when the API denies editing', async () => {
    vi.spyOn(api, 'dashboard').mockResolvedValue({ groups: [], revision: 'initial', canEdit: false, sources: [] })
    const catalog = vi.spyOn(api, 'dashboardSources')
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(<QueryClientProvider client={client}><MemoryRouter><Dashboard property={property} live={false} email={email} /></MemoryRouter></QueryClientProvider>)

    expect(await screen.findByRole('heading', { name: 'No monitoring widgets yet' })).toBeInTheDocument()
    expect(screen.getByText('A property operator can configure this dashboard.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Configure dashboard' })).not.toBeInTheDocument()
    expect(catalog).not.toHaveBeenCalled()
  })

  it('shows the backend storage diagnosis for a dashboard 503', async () => {
    vi.spyOn(api, 'dashboard').mockRejectedValue(new ApiError(503, undefined,
      'The dashboard could not read its database. Check the backend log for the MySQL error.'))
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(<QueryClientProvider client={client}><MemoryRouter><Dashboard property={property} live={false} email={email} /></MemoryRouter></QueryClientProvider>)
    expect(await screen.findByRole('alert')).toHaveTextContent('Check the backend log for the MySQL error.')
    expect(screen.queryByText('The dashboard database migration is required.')).not.toBeInTheDocument()
  })

  it('computes neutral stale text in both dashboard modes using the production stylesheet', async () => {
    vi.spyOn(Date, 'now').mockReturnValue(now)
    const daikin = { ...source, key: 'daikin:ac:Temperature', provider: 'daikin', deviceName: 'Kitchen AC', metric: 'temperature',
      unit: '°C', reading: reading(21, 5 * 60000 - 1) } satisfies DashboardSource
    const target = { ...daikin, key: 'daikin:ac:TargetTemperature', metric: 'targetTemperature',
      reading: reading(22, 5 * 60000) } satisfies DashboardSource
    const tuya = { ...source, key: 'tuya:thermometer:temp', provider: 'tuya', deviceName: 'Kitchen thermometer', metric: 'temperature',
      unit: '°C', reading: { ...reading(0, 0), observedAt: '2026-11-24T09:00:00+02:00', provenance: 'provider' } } satisfies DashboardSource
    const values = [daikin, target, tuya].map(item => ({ id: item.key, sourceKey: item.key, label: null }))
    const snapshot: DashboardSnapshot = { canEdit: false, revision: 'fixture', sources: [daikin, target, tuya],
      groups: [{ ...groups[0], widgets: [
        { ...groups[0].widgets[0], title: 'AC measurements', navigationSource: null, mode: 'compact', values: values.slice(0, 2) },
        { ...groups[0].widgets[1], title: 'Thermometer measurements', navigationSource: null, mode: 'detailed', values: values.slice(2) },
    ] }] }
    const load = vi.spyOn(api, 'dashboard').mockResolvedValue(snapshot)
    const catalog = vi.spyOn(api, 'dashboardSources')
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const browserStyles = readFileSync('src/styles.css', 'utf8')
      .replace(/^@import "tailwindcss";/m, '').replace(/@theme inline\s*\{[^}]*\}/, '')
    render(<QueryClientProvider client={client}><MemoryRouter><style>{browserStyles}</style>
      <Dashboard property={property} live={false} email={email} /></MemoryRouter></QueryClientProvider>)

    const global = within(await screen.findByRole('group', { name: 'Dashboard display mode' }))
    const compact = global.getByRole('button', { name: 'Compact' })
    const detailed = global.getByRole('button', { name: 'Detailed' })
    const acWidget = screen.getByRole('heading', { name: 'AC measurements' }).closest('article')!
    const sensorWidget = screen.getByRole('heading', { name: 'Thermometer measurements' }).closest('article')!
    expect(acWidget).toHaveClass('monitor-compact')
    expect(sensorWidget).toHaveClass('monitor-compact')
    expect(detailed).toHaveAttribute('aria-pressed', 'false')
    expect(screen.queryByRole('button', { name: 'Configure dashboard' })).not.toBeInTheDocument()
    fireEvent.click(compact)
    expect(sensorWidget).toHaveClass('monitor-compact')
    const old = within(sensorWidget).getByText('0 °C')
    expect(getComputedStyle(old).color).toBe('rgb(102, 102, 102)')
    expect(getComputedStyle(old.closest('.monitor-measure')!).backgroundColor).toBe('rgb(244, 244, 244)')
    fireEvent.click(detailed)
    expect(acWidget).toHaveClass('monitor-detailed')
    expect(detailed).toHaveAttribute('aria-pressed', 'true')
    expect(getComputedStyle(within(acWidget).getByText('21 °C')).color).toBe('rgb(0, 163, 68)')
    expect(getComputedStyle(within(acWidget).getByText('22 °C')).color).toBe('rgb(0, 106, 202)')
    expect(getComputedStyle(old).color).toBe('rgb(102, 102, 102)')
    expect(getComputedStyle(sensorWidget.querySelector('.monitor-measure-label')!).color).toBe('rgb(82, 82, 82)')
    expect(getComputedStyle(sensorWidget.querySelector('.monitor-measure-detail')!).color).toBe('rgb(82, 82, 82)')
    expect(old).toHaveAttribute('title', expect.stringContaining('2026-11-24T09:00:00+02:00'))
    expect(sensorWidget.querySelector('.monitor-measure-detail')).toHaveTextContent('Smart Life / Tuya · Kitchen thermometer')
    expect(sensorWidget.querySelector('.monitor-measure-detail')).toHaveTextContent('Provider observation')
    fireEvent.click(detailed)
    expect(acWidget).toHaveClass('monitor-detailed')
    expect(sensorWidget).toHaveClass('monitor-detailed')
    expect(load).toHaveBeenCalledTimes(1)
    expect(catalog).not.toHaveBeenCalled()
  })

  it('keeps all groups visible and only expands manually collapsed widgets on global Detailed', async () => {
    mockGridPositions(() => 1)
    const second = { ...groups[0], id: 'climate', title: 'Climate',
      widgets: groups[0].widgets.map((widget, index) => ({ ...widget, id: `climate-${index}`, title: `Climate widget ${index + 1}` })) }
    const load = vi.spyOn(api, 'dashboard').mockResolvedValue({ canEdit: false, revision: 'fixture', sources: [source], groups: [...groups, second] })
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const mount = (cache: QueryClient) => render(<QueryClientProvider client={cache}><MemoryRouter>
      <Dashboard property={property} live={false} email={email} />
    </MemoryRouter></QueryClientProvider>)
    const first = mount(client)

    const global = within(await screen.findByRole('group', { name: 'Dashboard display mode' }))
    const compact = global.getByRole('button', { name: 'Compact' })
    const detailed = global.getByRole('button', { name: 'Detailed' })
    expect(screen.getByRole('heading', { name: 'Climate widget 5' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Collapse Widget 1' })).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByRole('button', { name: 'Collapse Climate widget 1' })).toHaveAttribute('aria-expanded', 'true')
    fireEvent.click(screen.getByRole('button', { name: 'Collapse Climate widget 1' }))
    fireEvent.click(screen.getByRole('button', { name: 'Show detailed view for Climate widget 1' }))
    expect(screen.getByRole('button', { name: 'Show all Climate widget 1' })).toHaveAttribute('aria-expanded', 'false')
    fireEvent.click(compact)
    expect(screen.getByRole('button', { name: 'Show all Climate widget 1' })).toHaveAttribute('aria-expanded', 'false')
    expect(screen.getByRole('heading', { name: 'Climate widget 5' })).toBeInTheDocument()
    fireEvent.click(detailed)
    expect(screen.getByRole('button', { name: 'Collapse Climate widget 1' })).toHaveAttribute('aria-expanded', 'true')
    fireEvent.click(screen.getByRole('button', { name: 'Collapse Climate widget 1' }))
    fireEvent.click(detailed)
    expect(screen.getByRole('button', { name: 'Collapse Climate widget 1' })).toHaveAttribute('aria-expanded', 'true')
    fireEvent.click(screen.getByRole('button', { name: 'Collapse Widget 1' }))
    expect(load).toHaveBeenCalledTimes(1)

    first.unmount()
    mount(new QueryClient())
    expect(await screen.findByRole('button', { name: 'Show compact view for Climate widget 1' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Collapse Climate widget 1' })).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByRole('button', { name: 'Show all Widget 1' })).toHaveAttribute('aria-expanded', 'false')
  })

  it('restores independent views after navigation and isolates the same widget IDs by property and account', async () => {
    mockGridPositions(() => 1)
    const snapshot = { canEdit: false, revision: 'fixture', sources: [source], groups }
    const load = vi.spyOn(api, 'dashboard').mockResolvedValue(snapshot)
    const client = new QueryClient()
    const view = (activeProperty = property, account = email) => <QueryClientProvider client={client}><MemoryRouter>
      <Link to="/">Dashboard route</Link><Link to="/away">Away</Link>
      <Routes><Route path="/" element={<Dashboard property={activeProperty} live={false} email={account} />} />
        <Route path="/away" element={<h1>Away from dashboard</h1>} /></Routes>
    </MemoryRouter></QueryClientProvider>
    const { rerender } = render(view())
    fireEvent.click(await screen.findByRole('button', { name: 'Show detailed view for Widget 1' }))
    fireEvent.click(screen.getByRole('button', { name: 'Collapse Widget 2' }))
    fireEvent.click(screen.getByRole('link', { name: 'Away' }))
    expect(screen.getByRole('heading', { name: 'Away from dashboard' })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('link', { name: 'Dashboard route' }))
    expect(await screen.findByRole('button', { name: 'Show compact view for Widget 1' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Show all Widget 2' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Collapse Widget 1' })).toBeInTheDocument()

    rerender(view({ ...property, id: 4 }))
    await screen.findByRole('button', { name: 'Show detailed view for Widget 1' })
    fireEvent.click(screen.getByRole('button', { name: 'Collapse Widget 1' }))
    expect(screen.getByRole('button', { name: 'Collapse Widget 2' })).toBeInTheDocument()
    expect(load).toHaveBeenCalledWith(4, expect.anything())

    rerender(view(property, 'other@example.test'))
    await screen.findByRole('button', { name: 'Show detailed view for Widget 1' })
    expect(screen.getByRole('button', { name: 'Collapse Widget 2' })).toBeInTheDocument()
    rerender(view())
    expect(await screen.findByRole('button', { name: 'Show compact view for Widget 1' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Show all Widget 2' })).toBeInTheDocument()
    expect(Array.from({ length: localStorage.length }, (_, index) => localStorage.key(index)).join()).not.toContain(email)
  })

  it('retains saved choices while loading and discards preferences for deleted widgets on the next edit', async () => {
    mockGridPositions(() => 1)
    const snapshot = { canEdit: false, revision: 'fixture', sources: [source], groups }
    const load = vi.spyOn(api, 'dashboard').mockResolvedValue(snapshot)
    const mount = (client: QueryClient) => render(<QueryClientProvider client={client}><MemoryRouter>
      <Dashboard property={property} live={false} email={email} />
    </MemoryRouter></QueryClientProvider>)
    const initial = mount(new QueryClient())
    fireEvent.click(await screen.findByRole('button', { name: 'Show detailed view for Widget 1' }))
    fireEvent.click(screen.getByRole('button', { name: 'Collapse Widget 1' }))
    const key = localStorage.key(0)!
    const saved = localStorage.getItem(key)
    initial.unmount()

    let finishLoad!: (value: DashboardSnapshot) => void
    load.mockReturnValueOnce(new Promise(resolve => { finishLoad = resolve }))
    const write = vi.spyOn(Storage.prototype, 'setItem')
    const client = new QueryClient()
    mount(client)
    expect(screen.getByRole('status')).toHaveTextContent('Loading dashboard')
    expect(localStorage.getItem(key)).toBe(saved)
    await act(async () => finishLoad(snapshot))
    expect(await screen.findByRole('button', { name: 'Show compact view for Widget 1' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Show all Widget 1' })).toBeInTheDocument()

    const [deleted, ...remaining] = groups[0].widgets
    const updated = { ...snapshot, groups: [{ ...groups[0], widgets: remaining }] }
    act(() => { client.setQueryData(['property', property.id, 'dashboard'], updated) })
    await waitFor(() => expect(screen.queryByRole('heading', { name: deleted.title })).not.toBeInTheDocument())
    expect(write).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Show detailed view for Widget 2' }))
    expect(JSON.parse(localStorage.getItem(key)!)).not.toHaveProperty(deleted.id)
    act(() => { client.setQueryData(['property', property.id, 'dashboard'], {
      ...updated, groups: [{ ...groups[0], widgets: [{ ...deleted, id: 'recreated-widget' }, ...remaining] }],
    }) })
    expect(await screen.findByRole('button', { name: 'Show detailed view for Widget 1' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Collapse Widget 1' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Show compact view for Widget 2' })).toBeInTheDocument()
  })

  it('uses defaults and keeps controls usable when storage is corrupt or unavailable', async () => {
    const account = await webcrypto.subtle.digest('SHA-256', new TextEncoder().encode(email))
    localStorage.setItem(`dashboard-widget-views:v1:${Buffer.from(account).toString('hex')}:${property.id}`, '{bad json')
    vi.spyOn(api, 'dashboard').mockResolvedValue({ canEdit: false, revision: 'fixture', sources: [source], groups })
    const mount = () => render(<QueryClientProvider client={new QueryClient()}><MemoryRouter>
      <Dashboard property={property} live={false} email={email} />
    </MemoryRouter></QueryClientProvider>)
    const first = mount()
    expect(await screen.findByRole('button', { name: 'Show detailed view for Widget 1' })).toBeInTheDocument()
    first.unmount()

    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new DOMException('Unavailable', 'SecurityError') })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new DOMException('Unavailable', 'SecurityError') })
    mount()
    fireEvent.click(await screen.findByRole('button', { name: 'Show detailed view for Widget 1' }))
    expect(screen.getByRole('button', { name: 'Show compact view for Widget 1' })).toBeInTheDocument()
  })

  it('keeps independent widget modes across telemetry and reapplies global modes without restoring old choices', async () => {
    const snapshot: DashboardSnapshot = { canEdit: false, revision: 'fixture', sources: [source], groups: [
      { ...groups[0], collapsed: false, widgets: groups[0].widgets.slice(0, 3) },
    ] }
    const load = vi.spyOn(api, 'dashboard').mockResolvedValue(snapshot)
    const save = vi.spyOn(api, 'saveDashboard')
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(<QueryClientProvider client={client}><MemoryRouter><Dashboard property={property} live={false} email={email} /></MemoryRouter></QueryClientProvider>)
    const global = within(await screen.findByRole('group', { name: 'Dashboard display mode' }))
    const widgetButton = (index: number, mode: 'Compact' | 'Detailed') =>
      screen.getByRole('button', { name: `Show ${mode.toLowerCase()} view for Widget ${index}` })
    const modes = (expected: string[]) => expected.forEach((mode, index) => {
      expect(screen.getByRole('heading', { name: `Widget ${index + 1}` }).closest('article')).toHaveClass(`monitor-${mode}`)
    })

    fireEvent.click(widgetButton(1, 'Detailed'))
    modes(['detailed', 'compact', 'compact'])
    fireEvent.click(widgetButton(3, 'Detailed'))
    modes(['detailed', 'compact', 'detailed'])
    expect(widgetButton(3, 'Compact').querySelector('.lucide-rows-2')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Electrical' })).toBeInTheDocument()
    act(() => { client.setQueryData(['property', property.id, 'dashboard'], {
      ...snapshot, sources: [{ ...source, reading: { ...reading(42, 0), observedAt: new Date(Date.now() - 1000).toISOString() } }],
    }) })
    await waitFor(() => expect(screen.getAllByText('42 W').length).toBeGreaterThan(0))
    modes(['detailed', 'compact', 'detailed'])

    fireEvent.click(global.getByRole('button', { name: 'Compact' }))
    modes(['compact', 'compact', 'compact'])
    expect(widgetButton(1, 'Detailed').querySelector('.lucide-rows-3')).toBeInTheDocument()
    fireEvent.click(widgetButton(2, 'Detailed'))
    modes(['compact', 'detailed', 'compact'])
    expect(screen.getByRole('heading', { name: 'Electrical' })).toBeInTheDocument()
    fireEvent.click(global.getByRole('button', { name: 'Detailed' }))
    modes(['detailed', 'detailed', 'detailed'])
    fireEvent.click(widgetButton(3, 'Compact'))
    modes(['detailed', 'detailed', 'compact'])
    fireEvent.click(global.getByRole('button', { name: 'Detailed' }))
    modes(['detailed', 'detailed', 'detailed'])
    expect(load).toHaveBeenCalledTimes(1)
    expect(save).not.toHaveBeenCalled()
  })

  it('reorders draft measurements by pointer and keyboard controls without saving on Cancel', async () => {
    const snapshot: DashboardSnapshot = { canEdit: true, revision: 'draft', sources: [source], groups: [
      { ...groups[0], widgets: [{ ...groups[0].widgets[0], values: [
        { id: 'first', sourceKey: source.key, label: 'First', colorMode: 'fixed', customColor: '#985006' },
        { id: 'middle', sourceKey: source.key, label: 'Middle' },
        { id: 'last', sourceKey: source.key, label: 'Last' },
      ] }] },
    ] }
    vi.spyOn(api, 'dashboard').mockResolvedValue(snapshot)
    vi.spyOn(api, 'dashboardSources').mockResolvedValue([source])
    const save = vi.spyOn(api, 'saveDashboard').mockResolvedValue(undefined)
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const { container } = render(<QueryClientProvider client={client}><MemoryRouter>
      <Dashboard property={property} live={false} email={email} />
    </MemoryRouter></QueryClientProvider>)
    const order = () => Array.from(container.querySelectorAll('[data-dashboard-value]'))
      .map(item => item.getAttribute('data-dashboard-value'))
    const open = async () => {
      fireEvent.click(await screen.findByRole('button', { name: 'Configure dashboard' }))
      await waitFor(() => expect(screen.getByRole('button', { name: 'Drag measurement 1' })).toBeEnabled())
    }
    await open()
    const grip = screen.getByRole('button', { name: 'Drag measurement 1' })
    Object.defineProperty(grip, 'setPointerCapture', { value: vi.fn() })
    const original = Object.getOwnPropertyDescriptor(document, 'elementFromPoint')
    Object.defineProperty(document, 'elementFromPoint', { configurable: true, value: () => container.querySelector('[data-dashboard-value="last"]') })
    try {
      const pointer = { pointerId: 1, button: 0, pointerType: 'mouse', isPrimary: true }
      fireEvent.pointerDown(grip, pointer)
      fireEvent.pointerUp(grip, pointer)
      expect(order()).toEqual(['middle', 'last', 'first'])
      fireEvent.click(screen.getByRole('button', { name: 'Move measurement 3 up' }))
      expect(order()).toEqual(['middle', 'first', 'last'])
      expect(save).not.toHaveBeenCalled()
    } finally {
      if (original) Object.defineProperty(document, 'elementFromPoint', original)
      else Reflect.deleteProperty(document, 'elementFromPoint')
    }
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    await open()
    expect(order()).toEqual(['first', 'middle', 'last'])
    fireEvent.click(screen.getByRole('button', { name: 'Move measurement 1 down' }))
    fireEvent.click(screen.getByRole('button', { name: 'Save dashboard' }))
    await waitFor(() => expect(save).toHaveBeenCalledTimes(1))
    expect(save.mock.calls[0][2][0].widgets[0].values).toEqual([
      snapshot.groups[0].widgets[0].values[1],
      snapshot.groups[0].widgets[0].values[0],
      snapshot.groups[0].widgets[0].values[2],
    ])
  })

  it('creates, previews and saves a property layout from assigned sources', async () => {
    let snapshot: DashboardSnapshot = { groups: [], revision: 'initial', canEdit: true, sources: [] }
    vi.spyOn(api, 'dashboard').mockImplementation(async () => snapshot)
    vi.spyOn(api, 'dashboardSources').mockResolvedValue([source])
    const save = vi.spyOn(api, 'saveDashboard').mockImplementation(async (_, __, saved) => {
      snapshot = { ...snapshot, groups: saved, sources: [source] }
    }).mockRejectedValueOnce(new ApiError(503, undefined, 'Ask a database administrator to check storage permissions.'))
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(<QueryClientProvider client={client}><MemoryRouter><Dashboard property={property} live={false} email={email} /></MemoryRouter></QueryClientProvider>)
    fireEvent.click(await screen.findByRole('button', { name: 'Configure dashboard' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Add group' })).toBeEnabled())
    fireEvent.click(screen.getByRole('button', { name: 'Add group' }))
    fireEvent.change(screen.getByLabelText('Group title'), { target: { value: 'Utilities' } })
    fireEvent.click(screen.getByRole('button', { name: 'Add widget' }))
    fireEvent.change(screen.getByLabelText('Widget title'), { target: { value: 'Heater monitor' } })
    fireEvent.change(screen.getByLabelText('Provider'), { target: { value: source.provider } })
    fireEvent.change(screen.getByLabelText('Device'), { target: { value: source.deviceId } })
    fireEvent.change(screen.getByLabelText('Measurement', { exact: true }), { target: { value: source.key } })
    fireEvent.change(screen.getByLabelText('Display'), { target: { value: 'detailed' } })
    fireEvent.change(screen.getByLabelText('Widget width'), { target: { value: '4' } })
    fireEvent.change(screen.getByLabelText('Measurement columns'), { target: { value: '3' } })
    fireEvent.change(screen.getByLabelText('Value color'), { target: { value: '#985006' } })
    fireEvent.click(screen.getByText('Preview widget'))
    expect(screen.getByText('0 W')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /Open Heater/ })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Save dashboard' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Ask a database administrator to check storage permissions. Your edits are still open.')
    expect(screen.getByLabelText('Group title')).toHaveValue('Utilities')
    expect(screen.getByLabelText('Widget title')).toHaveValue('Heater monitor')
    expect(screen.getByLabelText('Measurement', { exact: true })).toHaveValue(source.key)
    fireEvent.click(screen.getByRole('button', { name: 'Save dashboard' }))
    const tile = await screen.findByRole('link', { name: /Open Heater/ })
    expect(tile).toHaveAttribute('href', source.route)
    expect(tile.querySelector('strong')).toHaveStyle({ color: '#985006' })
    expect(tile.closest('.monitor-grid-values')).toHaveStyle('--measurement-columns: 3')
    expect(tile.closest('article')?.parentElement).toHaveStyle('--widget-span: 4')
    expect(save).toHaveBeenCalledWith(3, 'initial', [expect.objectContaining({
      title: 'Utilities', widgets: [expect.objectContaining({ mode: 'detailed', width: 4, measurementColumns: 3,
        values: [expect.objectContaining({ colorMode: 'fixed', customColor: '#985006' })] })],
    })])
    fireEvent.click(screen.getByRole('button', { name: 'Configure dashboard' }))
    fireEvent.change(await screen.findByLabelText('Custom label'), { target: { value: 'Metered load' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save dashboard' }))
    await waitFor(() => expect(screen.queryByRole('form', { name: 'Dashboard editor' })).not.toBeInTheDocument())
    expect(screen.getByText('Metered load')).toBeInTheDocument()
    expect(save).toHaveBeenCalledTimes(3)
  })
})
