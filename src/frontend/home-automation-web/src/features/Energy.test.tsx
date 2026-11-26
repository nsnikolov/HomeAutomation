import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '../shared/api'
import type { Device, PropertyState } from '../shared/types'
import { Energy } from './Energy'

const property = { id: 3, name: 'Fixture', timeZone: 'Europe/Sofia' }
const inverter: Device = {
  id: 1, roomId: null, name: 'Growatt master', manufacturer: 'Growatt', model: 'SPF 5000 ES', kind: 'inverter', connectivity: 'online',
  faultCode: null, lastContactAt: null, quality: 'fresh',
  endpoints: [{ id: 11, name: 'Main', capabilities: [{ code: 'batteryPower', access: 'readOnly' }], measurements: [] }],
}
const relay: Device = { ...inverter, id: 2, name: 'Water heater relay', manufacturer: 'Tongou', kind: 'smartSwitch',
  endpoints: [{ id: 12, name: 'Main', capabilities: [{ code: 'powerMeasurement', access: 'readOnly' }], measurements: [] }] }
const state = (devices: Device[]): PropertyState => ({ devices, installation: [], alerts: [] })

afterEach(() => { vi.restoreAllMocks() })

function view(current?: PropertyState) {
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <Energy property={property} state={current} pollSeconds={300} />
  </QueryClientProvider>)
}

describe('energy without the simulator', () => {
  it('charts provider readings only and offers no simulator counter card', async () => {
    const history = vi.spyOn(api, 'history').mockResolvedValue([])
    view(state([inverter, relay]))
    expect(await screen.findByRole('heading', { name: 'Battery power' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: /Water heater energy counter/ })).not.toBeInTheDocument()
    expect(history).toHaveBeenCalledWith(3, 'batteryPower', 1, 11, expect.anything())
    expect(history).not.toHaveBeenCalledWith(3, 'energy', expect.anything(), expect.anything(), expect.anything())
    expect((await screen.findAllByText('No provider readings were recorded in this range.')).length).toBeGreaterThan(0)
    expect(screen.queryByText(/kWh|\d+ W/)).not.toBeInTheDocument()
  })

  it('says when no connected device reports battery power instead of inventing one', async () => {
    vi.spyOn(api, 'history').mockResolvedValue([])
    view(state([relay]))
    expect(await screen.findByText('No connected device reports battery power.')).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Battery power' })).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Solar and household power' })).toBeInTheDocument()
  })

  it('shows the latest recorded provider value with its gaps', async () => {
    vi.spyOn(api, 'history').mockImplementation(async (_, metric) => metric === 'pvPower'
      ? [{ observedAt: '2026-11-26T10:00:00Z', number: 2100, flag: null, code: null },
        { observedAt: '2026-11-26T10:05:00Z', number: 2200, flag: null, code: null }]
      : [])
    view(state([]))
    expect(await screen.findByText('2,200 W')).toBeInTheDocument()
    expect(screen.getAllByText('—').length).toBeGreaterThan(0)
  })
})
