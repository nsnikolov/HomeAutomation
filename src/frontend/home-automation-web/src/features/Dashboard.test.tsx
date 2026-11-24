import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '../shared/api'
import type { DashboardGroup, DashboardReading, DashboardSnapshot, DashboardSource } from '../shared/types'
import { Dashboard } from './Dashboard'
import { DashboardGroups, dashboardFreshness, useDashboardTime } from './DashboardWidgets'

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

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

describe('configurable dashboard', () => {
  it('uses the exact color boundaries and treats zero and textual readings as valid', () => {
    expect(dashboardFreshness(reading(0, 5 * 60000 - 1), now)).toBe('fresh')
    expect(dashboardFreshness(reading(0, 5 * 60000), now)).toBe('aging')
    expect(dashboardFreshness({ ...reading(0, 60 * 60000 - 1), number: null, flag: false }, now)).toBe('aging')
    expect(dashboardFreshness({ ...reading(0, 60 * 60000), number: null, code: 'heating' }, now)).toBe('old')
    expect(dashboardFreshness(null, now)).toBe('missing')
    expect(dashboardFreshness(reading(Infinity, 1), now)).toBe('missing')
    expect(dashboardFreshness(reading(2, -1), now)).toBe('missing')
  })

  it('advances freshness without telemetry or a refetch', () => {
    vi.useFakeTimers()
    vi.setSystemTime(now)
    const timed = { ...source, reading: reading(0, 5 * 60000 - 1) }
    const sources = [timed]
    function Clock() { return <output>{dashboardFreshness(timed.reading, useDashboardTime(sources))}</output> }
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
    vi.spyOn(HTMLElement.prototype, 'getClientRects').mockReturnValue({ length: 1 } as DOMRectList)
    vi.spyOn(HTMLElement.prototype, 'offsetTop', 'get').mockImplementation(function (this: HTMLElement) {
      return Math.floor(Array.from(this.parentElement?.children ?? []).indexOf(this) / columns) * 50
    })
    vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(44)
    const view = (sample: DashboardSource) => <MemoryRouter>
      <DashboardGroups groups={groups} sources={new Map([[sample.key, sample]])} property={property} now={now} />
    </MemoryRouter>
    const { rerender } = render(view(source))
    expect(screen.queryByRole('heading', { name: 'Widget 5' })).not.toBeInTheDocument()
    const groupControl = screen.getByRole('button', { name: 'Show all Electrical' })
    const first = screen.getByRole('heading', { name: 'Widget 1' }).closest('article')!
    expect(within(first).getAllByText('0 W')[0]).toHaveClass('monitor-age-aging')
    expect(within(first).getByText('—')).toHaveAttribute('title', expect.stringContaining('no longer available'))
    const widgetControl = within(first).getByRole('button', { name: 'Show all Widget 1' })
    expect(widgetControl).toHaveAttribute('aria-expanded', 'false')
    fireEvent.click(widgetControl)
    expect(within(first).getByRole('button', { name: 'Collapse Widget 1' })).toHaveAttribute('aria-expanded', 'true')
    expect(groupControl).toHaveAttribute('aria-expanded', 'false')
    fireEvent.click(groupControl)
    expect(screen.getByRole('heading', { name: 'Widget 5' })).toBeInTheDocument()
    rerender(view({ ...source, reading: reading(42, 100) }))
    expect(within(first).getAllByText('42 W')[0]).toHaveClass('monitor-age-fresh')
    expect(screen.getByRole('button', { name: 'Collapse Electrical' })).toBeInTheDocument()
    expect(within(first).getByRole('button', { name: 'Collapse Widget 1' })).toBeInTheDocument()
    act(() => { columns = 3; observers.forEach(callback => callback()) })
    expect(screen.queryByRole('button', { name: 'Collapse Electrical' })).not.toBeInTheDocument()
    act(() => { columns = 1; observers.forEach(callback => callback()) })
    expect(screen.getByRole('button', { name: 'Collapse Electrical' })).toBeInTheDocument()
  })

  it('only navigates to the configured internal provider page', () => {
    const target = { ...groups[0], widgets: [groups[0].widgets[0]] }
    render(<MemoryRouter initialEntries={['/']}>
      <Routes>
        <Route path="/" element={<DashboardGroups groups={[target]} sources={new Map([[source.key, source]])}
          property={property} now={now} />} />
        <Route path={source.route} element={<h1>Provider detail</h1>} />
      </Routes>
    </MemoryRouter>)
    expect(screen.queryByRole('button', { name: /on|off|switch/i })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('link', { name: 'Widget 1' }))
    expect(screen.getByRole('heading', { name: 'Provider detail' })).toBeInTheDocument()
  })

  it('keeps an empty dashboard read-only when the API denies editing', async () => {
    vi.spyOn(api, 'dashboard').mockResolvedValue({ groups: [], revision: 'initial', canEdit: false, sources: [] })
    const catalog = vi.spyOn(api, 'dashboardSources')
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(<QueryClientProvider client={client}><MemoryRouter><Dashboard property={property} live={false} /></MemoryRouter></QueryClientProvider>)

    expect(await screen.findByRole('heading', { name: 'No monitoring widgets yet' })).toBeInTheDocument()
    expect(screen.getByText('A property operator can configure this dashboard.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Configure dashboard' })).not.toBeInTheDocument()
    expect(catalog).not.toHaveBeenCalled()
  })

  it('creates, previews and saves a property layout from assigned sources', async () => {
    let snapshot: DashboardSnapshot = { groups: [], revision: 'initial', canEdit: true, sources: [] }
    vi.spyOn(api, 'dashboard').mockImplementation(async () => snapshot)
    vi.spyOn(api, 'dashboardSources').mockResolvedValue([source])
    const save = vi.spyOn(api, 'saveDashboard').mockImplementation(async (_, __, saved) => {
      snapshot = { ...snapshot, groups: saved, sources: [source] }
    })
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(<QueryClientProvider client={client}><MemoryRouter><Dashboard property={property} live={false} /></MemoryRouter></QueryClientProvider>)
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
    fireEvent.change(screen.getByLabelText('Open on click'), { target: { value: source.key } })
    fireEvent.click(screen.getByText('Preview widget'))
    expect(screen.getByText('0 W')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Heater monitor' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Save dashboard' }))
    expect(await screen.findByRole('link', { name: 'Heater monitor' })).toHaveAttribute('href', source.route)
    expect(save).toHaveBeenCalledWith(3, 'initial', [expect.objectContaining({
      title: 'Utilities', widgets: [expect.objectContaining({ mode: 'detailed', navigationSource: source.key })],
    })])
    fireEvent.click(screen.getByRole('button', { name: 'Configure dashboard' }))
    fireEvent.change(await screen.findByLabelText('Custom label'), { target: { value: 'Metered load' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save dashboard' }))
    await waitFor(() => expect(screen.queryByRole('form', { name: 'Dashboard editor' })).not.toBeInTheDocument())
    expect(screen.getByText('Metered load')).toBeInTheDocument()
    expect(save).toHaveBeenCalledTimes(2)
  })
})
