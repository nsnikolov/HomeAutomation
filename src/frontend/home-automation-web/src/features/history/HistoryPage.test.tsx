import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, LogicalApiError, type LogicalHistoryRequest } from '../../shared/api'
import type { HistoryBucket, LogicalDevice, LogicalHistory, LogicalHistorySegment, LogicalMetric } from '../../shared/types'
import { ChartSizeContext } from './HistoryCharts'
import { HistoryPage } from './HistoryPage'
import { bucket, segment } from './historyFixtures'

const property = { id: 3, name: 'Countryside House', timeZone: 'Europe/Sofia' }
const now = Date.parse('2026-11-26T20:00:00Z')
const metric = (name: LogicalMetric['metric'], unit: string | null, aggregation: LogicalMetric['aggregation'], current = true): LogicalMetric =>
  ({ channelKey: 'main', metric: name, unit, aggregation, current })
const device = (id: number, name: string, metrics: LogicalMetric[]): LogicalDevice => ({
  id, name, kind: 'Sensor', roomId: null, description: null, createdAt: '2026-11-01T00:00:00Z', updatedAt: '2026-11-01T00:00:00Z',
  archivedAt: null, current: [], previous: [], metrics,
})
const devices = [
  device(4, 'Living room', [metric('temperature', '°C', 'gauge'), metric('humidity', '%', 'gauge', false)]),
  device(5, 'Outdoor', [metric('temperature', '°C', 'gauge')]),
  device(6, 'Heat pump meter', [metric('activePower', 'W', 'gauge'), metric('energy', 'kWh', 'cumulativeCounter')]),
  device(7, 'Boiler relay', [metric('switchOn', null, 'boolean')]),
]
const replacements: LogicalHistorySegment[] = [segment(1, 'Tuya A', '1970-01-01T00:00:00Z', '2026-11-26T14:37:00Z'),
  segment(2, 'eWeLink B', '2026-11-26T14:37:00Z', '2026-11-26T16:20:00Z'), segment(3, 'Sonoff C', '2026-11-26T16:20:00Z', null)]

function history(id: number, request: LogicalHistoryRequest, buckets: HistoryBucket[] | null, extra: Partial<LogicalHistory> = {}): LogicalHistory {
  const units: Record<string, string | null> = { temperature: '°C', humidity: '%', activePower: 'W', energy: 'kWh', switchOn: null }
  const aggregation = request.metric === 'energy' ? 'cumulativeCounter' : request.metric === 'switchOn' ? 'boolean' : 'gauge'
  return { logicalDeviceId: id, name: devices.find(item => item.id === id)!.name, channelKey: 'main', metric: request.metric as LogicalMetric['metric'],
    unit: units[request.metric], aggregation, resolution: request.resolution, from: request.from ?? '2026-11-25T20:00:00Z',
    to: request.to ?? '2026-11-26T20:00:00Z', timeZone: property.timeZone, incompleteBelowCoverage: 0.8, segments: [segment(id * 10, 'Sensor', '1970-01-01T00:00:00Z', null)],
    points: null, buckets, nextAfter: null, ...extra }
}
const temperatureBuckets = [
  bucket('2026-11-26T14:00:00Z', { average: 21, minimum: 20, maximum: 24, coverage: 0.94, coveredSeconds: 3384, flags: ['deviceReplaced'] }),
  bucket('2026-11-26T15:00:00Z', { average: null, minimum: null, maximum: null, quality: 'noData', coverage: 0, coveredSeconds: 0, sampleCount: 0 }),
  bucket('2026-11-26T16:00:00Z', { average: 0, minimum: 0, maximum: 0, quality: 'incomplete', coverage: 0.5, coveredSeconds: 1800, flags: ['deviceReplaced'] }),
  bucket('2026-11-26T17:00:00Z', { average: null, minimum: null, maximum: null, quality: 'notAggregated', coverage: 0, coveredSeconds: 0, sampleCount: 0 }),
]

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['Date'] })
  vi.setSystemTime(now)
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
  vi.spyOn(api, 'logicalDevices').mockResolvedValue({ canManage: false, devices })
})
afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

function view(search = '') {
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <ChartSizeContext.Provider value={{ width: 800, height: 280 }}>
      <MemoryRouter initialEntries={[`/properties/3/history${search}`]}>
        <Routes><Route path="/properties/:propertyId/history" element={<HistoryPage property={property} />} /></Routes>
      </MemoryRouter>
    </ChartSizeContext.Provider>
  </QueryClientProvider>)
}

const periodTable = () => screen.getByRole('table', { name: /by period/ })

describe('history page', () => {
  it('shows a continuous logical temperature history with gaps, zero values, quality and replacement markers', async () => {
    const read = vi.spyOn(api, 'logicalHistory').mockImplementation(async (_, id, request) =>
      history(id, request, temperatureBuckets, { segments: replacements }))
    view()

    expect(await screen.findByRole('heading', { name: 'Living room · Temperature' })).toBeInTheDocument()
    expect(await screen.findByRole('img', { name: /average with minimum and maximum/ })).toBeInTheDocument()
    expect(read).toHaveBeenCalledWith(3, 4, { channel: 'main', metric: 'temperature', resolution: 'hour',
      from: '2026-11-25T20:00:00.000Z', to: '2026-11-26T20:00:00.000Z' }, expect.anything())
    const cells = within(periodTable()).getAllByRole('cell').map(cell => cell.textContent)
    expect(cells).toEqual(['Average 21 °C, minimum 20 °C, maximum 24 °C', 'No value · No reading reported',
      'Average 0 °C, minimum 0 °C, maximum 0 °C · Incomplete coverage (50%)', 'No value · Not yet aggregated'])
    const replaced = within(screen.getByRole('list', { name: 'Device replacements' })).getAllByRole('listitem').map(item => item.textContent)
    expect(replaced[0]).toMatch(/Tuya A → eWeLink B$/)
    expect(replaced[1]).toMatch(/eWeLink B → Sonoff C$/)
    expect(replaced[0]).toMatch(/4:37/)
    const quality = within(screen.getByRole('list', { name: 'Data quality' })).getAllByRole('listitem').map(item => item.textContent)
    expect(quality).toEqual(['Complete: 1', 'Incomplete coverage: 1', 'No reading reported: 1', 'Not yet aggregated: 1'])
    const summary = screen.getByText('Average').closest('dl')!
    expect(within(summary).getByText(/13\.7 °C/)).toBeInTheDocument()
    // The zero-valued hour is a real reading, so it is the range minimum; the missing hours are not zeros.
    expect(within(summary).getByText('Minimum').nextElementSibling).toHaveTextContent('0 °C')
    expect(within(summary).getByText('Maximum').nextElementSibling).toHaveTextContent('24 °C')
    expect(within(summary).getByText('36%')).toBeInTheDocument()
    expect(within(summary).getByText('part of the range is not aggregated yet')).toBeInTheDocument()
    expect(screen.getByText('Sources: 3 physical devices')).toBeInTheDocument()
  })

  it('requests property-local calendar days for 30 days and builds months for 12 months', async () => {
    const read = vi.spyOn(api, 'logicalHistory').mockImplementation(async (_, id, request) => history(id, request, request.resolution === 'day' ? [
      bucket('2026-10-30T22:00:00Z', { date: '2026-10-31', durationSeconds: 24 * 3600 }),
      bucket('2026-10-31T22:00:00Z', { date: '2026-11-01', durationSeconds: 24 * 3600, average: 10 }),
      bucket('2026-11-01T22:00:00Z', { date: '2026-11-02', durationSeconds: 24 * 3600, average: 30 }),
    ] : temperatureBuckets))
    view()
    await screen.findByRole('heading', { name: 'Living room · Temperature' })

    fireEvent.change(screen.getByLabelText('Time range'), { target: { value: '30d' } })
    await waitFor(() => expect(read).toHaveBeenLastCalledWith(3, 4,
      { channel: 'main', metric: 'temperature', resolution: 'day', fromDate: '2026-10-28', toDate: '2026-11-26' }, expect.anything()))
    expect(await within(await screen.findByRole('table', { name: /by period/ })).findAllByRole('row')).toHaveLength(4)

    fireEvent.change(screen.getByLabelText('Time range'), { target: { value: '12m' } })
    await waitFor(() => expect(read).toHaveBeenLastCalledWith(3, 4,
      { channel: 'main', metric: 'temperature', resolution: 'day', fromDate: '2025-12-01', toDate: '2026-11-26' }, expect.anything()))
    await waitFor(() => expect(within(periodTable()).getAllByRole('row')).toHaveLength(3))
    const november = within(periodTable()).getAllByRole('row')[2]
    expect(november).toHaveTextContent(/Nov 2026/)
    expect(november).toHaveTextContent('Average 20 °C, minimum 18 °C, maximum 22 °C')
    expect(screen.getByLabelText('Resolution')).toHaveValue('month')
  })

  it('applies a custom range entered in property-local time', async () => {
    const read = vi.spyOn(api, 'logicalHistory').mockImplementation(async (_, id, request) => history(id, request, temperatureBuckets))
    view()
    await screen.findByRole('heading', { name: 'Living room · Temperature' })
    fireEvent.change(screen.getByLabelText('Time range'), { target: { value: 'custom' } })
    const form = screen.getByRole('form', { name: 'Custom range' })
    fireEvent.change(within(form).getByLabelText('From'), { target: { value: '2026-10-25T00:00' } })
    fireEvent.change(within(form).getByLabelText('To'), { target: { value: '2026-10-26T00:00' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Apply range' }))

    await waitFor(() => expect(read).toHaveBeenLastCalledWith(3, 4, { channel: 'main', metric: 'temperature', resolution: 'hour',
      from: '2026-10-24T21:00:00.000Z', to: '2026-10-25T22:00:00.000Z' }, expect.anything()))
  })

  it('shows measured counter energy, marks partial periods and never adds estimates to it', async () => {
    vi.spyOn(api, 'logicalHistory').mockImplementation(async (_, id, request) => history(id, request, [
      bucket('2026-11-26T14:00:00Z', { energy: { status: 'measured', basis: 'counter', wh: 1000, positiveWh: null, negativeWh: null } }),
      bucket('2026-11-26T15:00:00Z', { quality: 'incomplete', coverage: 0.5, coveredSeconds: 1800,
        energy: { status: 'incomplete', basis: 'counter', wh: 500, positiveWh: null, negativeWh: null } }),
    ]))
    view('?device=6&metric=energy&channel=main')

    expect(await screen.findByRole('heading', { name: 'Heat pump meter · Energy counter' })).toBeInTheDocument()
    expect(within(await screen.findByRole('table', { name: /by period/ })).getAllByRole('cell').map(cell => cell.textContent)).toEqual(['Measured 1 kWh', 'Measured 0.5 kWh (partial) · Incomplete coverage (50%)'])
    const summary = screen.getByText('Measured energy').closest('div')!
    expect(summary).toHaveTextContent('1.5 kWh')
    expect(summary).toHaveTextContent('partial, device counter')
    expect(screen.queryByText('Estimated energy')).not.toBeInTheDocument()
    expect(screen.getByText(/measured by the device counter/)).toBeInTheDocument()
  })

  it('shows power with a separately labelled estimated energy chart', async () => {
    vi.spyOn(api, 'logicalHistory').mockImplementation(async (_, id, request) => history(id, request, [
      bucket('2026-11-26T14:00:00Z', { average: 500, minimum: 0, maximum: 1200,
        energy: { status: 'estimated', basis: 'powerIntegration', wh: null, positiveWh: 500, negativeWh: 0 } }),
      bucket('2026-11-26T15:00:00Z', { average: 800, minimum: 600, maximum: 900, quality: 'incomplete', coverage: 0.25, coveredSeconds: 900,
        energy: { status: 'incomplete', basis: 'powerIntegration', wh: null, positiveWh: 200, negativeWh: 0 } }),
    ]))
    view('?device=6&metric=activePower')

    const section = await screen.findByRole('region', { name: 'Estimated energy' })
    expect(within(section).getByText(/never combined with measured energy/)).toBeInTheDocument()
    expect(within(section).getAllByRole('cell').map(cell => cell.textContent)).toEqual(['Estimated 0.5 kWh', 'Estimated 0.2 kWh (partial) · Incomplete coverage (25%)'])
    const summary = screen.getByText('Estimated energy', { selector: 'dt' }).closest('div')!
    expect(summary).toHaveTextContent('0.7 kWh')
    expect(summary).toHaveTextContent('partial, from power readings')
    expect(screen.getByText('Maximum').nextElementSibling).toHaveTextContent('1,200 W')
  })

  it('shows relay ON time against covered time', async () => {
    vi.spyOn(api, 'logicalHistory').mockImplementation(async (_, id, request) => history(id, request, [
      bucket('2026-11-26T14:00:00Z', { average: null, minimum: null, maximum: null, onSeconds: 1800, coveredSeconds: 3600 }),
      bucket('2026-11-26T15:00:00Z', { average: null, minimum: null, maximum: null, onSeconds: 0, coveredSeconds: 600, coverage: 0.17, quality: 'incomplete' }),
    ]))
    view('?device=7')

    expect(await screen.findByRole('img', { name: /ON, OFF and uncovered hours/ })).toBeInTheDocument()
    expect(within(periodTable()).getAllByRole('cell').map(cell => cell.textContent)).toEqual(['ON 30 min of 1 h 0 min',
      'ON 0 min of 10 min · Incomplete coverage (17%)'])
    expect(screen.getByText('ON duration').nextElementSibling).toHaveTextContent('30 min')
    expect(screen.getByText('Covered duration').nextElementSibling).toHaveTextContent('1 h 10 min')
  })

  it('compares devices on a shared axis while keeping each one separate', async () => {
    const read = vi.spyOn(api, 'logicalHistory').mockImplementation(async (_, id, request) => history(id, request,
      id === 4 ? temperatureBuckets : temperatureBuckets.map(item => ({ ...item, average: item.average === null ? null : item.average - 15 }))))
    view()
    fireEvent.click(await screen.findByLabelText(/Outdoor/))

    await waitFor(() => expect(read).toHaveBeenCalledWith(3, 5, expect.objectContaining({ metric: 'temperature', resolution: 'hour' }), expect.anything()))
    await waitFor(() => expect(within(periodTable()).getAllByRole('columnheader').map(cell => cell.textContent)).toEqual(['Period', 'Living room', 'Outdoor']))
    expect(within(periodTable()).getAllByRole('row')[1]).toHaveTextContent('Average 6 °C')
    expect(within(screen.getByRole('list', { name: 'Comparison coverage' })).getByText(/Outdoor: average/)).toBeInTheDocument()
    expect(screen.queryByLabelText(/Heat pump meter/)).not.toBeInTheDocument()
  })

  it('draws raw readings and uses hourly buckets for the summary', async () => {
    const read = vi.spyOn(api, 'logicalHistory').mockImplementation(async (_, id, request) => request.resolution === 'raw'
      ? history(id, request, null, { segments: replacements, nextAfter: '2026-11-26T15:00:00Z', points: [
        { observedAt: '2026-11-26T14:30:00Z', receivedAt: '2026-11-26T14:30:00Z', number: 20, flag: null, code: null, timestampSource: 'provider', seriesId: 1, assignmentId: 1 },
        { observedAt: '2026-11-26T14:40:00Z', receivedAt: '2026-11-26T14:40:00Z', number: 24, flag: null, code: null, timestampSource: 'provider', seriesId: 2, assignmentId: 2 },
      ] }) : history(id, request, temperatureBuckets))
    view('?res=raw')

    expect(await screen.findByRole('img', { name: /individual readings/ })).toBeInTheDocument()
    expect(read).toHaveBeenCalledWith(3, 4, expect.objectContaining({ resolution: 'raw', limit: 5000 }), expect.anything())
    expect(read).toHaveBeenCalledWith(3, 4, expect.objectContaining({ resolution: 'hour' }), expect.anything())
    expect(screen.getByText(/Showing the first 5,000 readings/)).toBeInTheDocument()
    expect(await screen.findByText(/13\.7 °C/)).toBeInTheDocument()
    expect(screen.queryByRole('group', { name: /Compare with/ })).not.toBeInTheDocument()
  })

  it('explains a measurement the replacement device does not report', async () => {
    vi.spyOn(api, 'logicalHistory').mockImplementation(async (_, id, request) => history(id, request, [
      bucket('2026-11-26T14:00:00Z', { quality: 'incomplete', coverage: 0.6, coveredSeconds: 2220, flags: ['deviceReplaced', 'notReported'] }),
      bucket('2026-11-26T15:00:00Z', { average: null, minimum: null, maximum: null, quality: 'noData', coverage: 0, coveredSeconds: 0, flags: ['notReported'] }),
    ], { segments: [segment(1, 'Tuya A', '1970-01-01T00:00:00Z', '2026-11-26T14:37:00Z'),
      { ...segment(2, 'eWeLink B', '2026-11-26T14:37:00Z', null), status: 'unsupported', seriesId: null }] }))
    view('?device=4&metric=humidity')

    expect(await screen.findByRole('note')).toHaveTextContent(/eWeLink B does not report this measurement from .*Earlier readings stay available/)
    expect(within(periodTable()).getAllByRole('cell')[1]).toHaveTextContent('Not reported by the assigned device')
    expect(screen.getByRole('option', { name: /Relative humidity.*not reported by the current device/ })).toBeInTheDocument()
  })
})

describe('history availability', () => {
  it('reports history as not yet available when the server lacks the migrations', async () => {
    vi.mocked(api.logicalDevices).mockRejectedValue(new LogicalApiError(503, undefined, 'pending'))
    view()
    expect(await screen.findByText('History is not available yet.')).toBeInTheDocument()
    expect(screen.queryByText(/No readings were recorded/)).not.toBeInTheDocument()
  })

  it('distinguishes an unavailable history endpoint, an empty range and an unknown failure', async () => {
    const read = vi.spyOn(api, 'logicalHistory').mockRejectedValue(new LogicalApiError(503, undefined, 'pending'))
    const { unmount } = view()
    expect(await screen.findByText('History is not available yet.')).toBeInTheDocument()
    unmount()

    read.mockImplementation(async (_, id, request) => history(id, request, [bucket('2026-11-26T14:00:00Z',
      { average: null, minimum: null, maximum: null, quality: 'noData', coverage: 0, coveredSeconds: 0, sampleCount: 0 })]))
    const second = view()
    expect(await screen.findByText('No readings were recorded in this range.')).toBeInTheDocument()
    second.unmount()

    read.mockRejectedValue(new Error('network'))
    view()
    expect(await screen.findByText('History could not be loaded. Try again later.')).toBeInTheDocument()
  })

  it('explains how to create logical devices when none exist', async () => {
    vi.mocked(api.logicalDevices).mockResolvedValue({ canManage: true, devices: [] })
    view()
    expect(await screen.findByText(/documented initialization command/)).toBeInTheDocument()
  })
})
