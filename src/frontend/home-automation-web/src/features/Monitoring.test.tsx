import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it } from 'vitest'
import { Dashboard } from './Dashboard'
import { GrowattSummary } from './GrowattPanel'
import { DeviceBadge } from './Devices'
import { mergeHistory } from './Energy'
import type { Device, Point, Reading } from '../shared/types'

const property = { id: 1, name: 'Countryside House', timeZone: 'Europe/Sofia' }
const meter: Device = {
  id: 5, roomId: 1, name: 'Heater meter', manufacturer: 'Sonoff', model: 'Simulated switch',
  kind: 'smartSwitch', connectivity: 'online', faultCode: null, lastContactAt: null, quality: 'fresh', endpoints: [],
}

describe('Monitoring dashboard', () => {
  it('shows the unconfigured Growatt state without rendering fabricated readings', () => {
    render(<GrowattSummary result={{ status: 'notConfigured', data: null }} timeZone="Europe/Sofia" />)
    expect(screen.getByText(/Live Growatt monitoring is not configured/)).toBeInTheDocument()
    expect(screen.queryByText('Solar installation')).not.toBeInTheDocument()
  })

  it('shows current data, known faults, and unknown grid rather than an outage for stale readings', () => {
    const reading = { unit: null, observedAt: null, number: null, flag: false, code: null, quality: 'stale' } as const
    const installation: Reading[] = [{ ...reading, metric: 'gridAvailable' }, {
      ...reading, metric: 'pvPower', unit: 'W', number: 3200, flag: null, quality: 'fresh',
    }]
    render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MemoryRouter>
        <Dashboard property={property} state={{ devices: [{ ...meter, faultCode: 'SIM_METER_FAULT' }], installation, alerts: [] }} loading={false} />
      </MemoryRouter>
    </QueryClientProvider>)
    expect(screen.getByRole('heading', { name: 'Dashboard' })).toBeInTheDocument()
    expect(screen.getByText('3.20 kW')).toBeInTheDocument()
    expect(screen.getByText('Fault')).toBeInTheDocument()
    const grid = screen.getByRole('heading', { name: 'Grid availability' }).closest('section')
    expect(within(grid!).getByText('Reading is stale')).toBeInTheDocument()
    expect(within(grid!).queryByText('Outage')).not.toBeInTheDocument()
  })

  it('gives connectivity and telemetry freshness distinct labels', () => {
    render(<><DeviceBadge device={{ ...meter, connectivity: 'offline' }} /><DeviceBadge device={{ ...meter, quality: 'stale' }} /></>)
    expect(screen.getByText('Offline')).toBeInTheDocument()
    expect(screen.getByText('Stale')).toBeInTheDocument()
  })

  it('inserts gaps instead of inventing numeric history', () => {
    const points: Point[] = [
      { observedAt: '2026-11-21T12:00:00Z', number: 50, flag: null, code: null },
      { observedAt: '2026-11-21T12:01:00Z', number: 10, flag: null, code: null },
    ]
    expect(mergeHistory([{ metric: 'pvPower', points }], 10).map(point => point.pvPower)).toEqual([50, null, 10])
  })
})
