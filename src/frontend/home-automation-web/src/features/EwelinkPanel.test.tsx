import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { expect, it, vi } from 'vitest'
import { api } from '../shared/api'
import { EwelinkPanel } from './EwelinkPanel'

const property = { id: 1, name: 'Fixture', timeZone: 'Europe/Sofia' }

function view(path = '/') {
  return render(<QueryClientProvider client={new QueryClient()}>
    <MemoryRouter initialEntries={[path]}><EwelinkPanel property={property} /></MemoryRouter>
  </QueryClientProvider>)
}

it('shows the automatic OAuth outcome without a local completion action', async () => {
  const status = vi.spyOn(api, 'ewelink').mockResolvedValue({
    status: 'connected', manage: true, lastSuccess: null, nextSync: null, devices: [], unassigned: [],
  })
  const rooms = vi.spyOn(api, 'rooms').mockResolvedValue([])
  try {
    view('/properties/1/ewelink?connect=success')
    expect(await screen.findByRole('button', { name: 'Disconnect account' })).toBeInTheDocument()
    expect(screen.getByText('eWeLink authorization completed.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Finish connecting' })).not.toBeInTheDocument()
  } finally {
    status.mockRestore()
    rooms.mockRestore()
  }
})

it('shows the approval boundary and hides account actions without configuration', async () => {
  const status = vi.spyOn(api, 'ewelink').mockResolvedValue({
    status: 'notConfigured', manage: false, lastSuccess: null, nextSync: null, devices: [], unassigned: [],
  })
  try {
    view()
    expect(await screen.findByText(/needs approved Developer credentials/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Connect account' })).not.toBeInTheDocument()
  } finally { status.mockRestore() }
})

it('shows offline and last-known state without offering physical controls', async () => {
  const timestamp = '2026-11-22T12:00:00Z'
  const status = vi.spyOn(api, 'ewelink').mockResolvedValue({
    status: 'partial', manage: false, lastSuccess: timestamp, nextSync: null, unassigned: [],
    devices: [{ id: 'fixture', name: 'Test relay', model: 'Fixture', roomId: 1, connectivity: 'offline',
      seenAt: timestamp, quality: 'fresh', hasRelay: true, relay: true, relayAt: timestamp, relayQuality: 'stale',
      supportedMetrics: ['temperature', 'humidity', 'activePower'],
      measurements: [
        { metric: 'temperature', unit: '°C', number: 12.9, flag: null, code: null, observedAt: timestamp, quality: 'stale' },
        { metric: 'activePower', unit: 'W', number: 200, flag: null, code: null, observedAt: timestamp, quality: 'stale' },
      ],
    }],
  })
  try {
    view()
    expect(await screen.findByRole('heading', { name: 'Test relay' })).toBeInTheDocument()
    expect(screen.getByText('Offline')).toBeInTheDocument()
    expect(screen.getByText('On · Last known')).toBeInTheDocument()
    expect(screen.getByText('12.9 °C · Last known')).toBeInTheDocument()
    expect(screen.getByText('200 W · Last known')).toBeInTheDocument()
    expect(screen.getByText('Relative humidity').nextElementSibling).toHaveTextContent('Unavailable')
    expect(screen.queryByText('Energy today')).not.toBeInTheDocument()
    expect(screen.queryByText('Cumulative energy')).not.toBeInTheDocument()
    expect(screen.queryByText('Voltage')).not.toBeInTheDocument()
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  } finally { status.mockRestore() }
})

it.each([true, false])('shows only capabilities on a device with relay support set to %s', async hasRelay => {
  const status = vi.spyOn(api, 'ewelink').mockResolvedValue({
    status: 'connected', manage: false, lastSuccess: null, nextSync: null, unassigned: [],
    devices: [{ id: 'fixture', name: 'Basic device', model: '', roomId: 1, connectivity: 'online',
      seenAt: '2026-11-22T12:00:00Z', quality: 'fresh', hasRelay, relay: hasRelay ? false : null,
      relayAt: null, relayQuality: hasRelay ? 'fresh' : 'missing', supportedMetrics: [], measurements: [] }],
  })
  try {
    view()
    expect(await screen.findByRole('heading', { name: 'Basic device' })).toBeInTheDocument()
    expect(screen.getByText('Online')).toBeInTheDocument()
    const rows = screen.queryAllByRole('term')
    expect(rows.map(row => row.textContent)).toEqual(hasRelay ? ['Relay'] : [])
  } finally { status.mockRestore() }
})
