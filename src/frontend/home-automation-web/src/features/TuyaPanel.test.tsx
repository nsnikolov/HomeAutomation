import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { expect, it, vi } from 'vitest'
import { api } from '../shared/api'
import { TuyaPanel } from './TuyaPanel'

const property = { id: 1, name: 'Fixture', timeZone: 'Europe/Sofia' }

function view() {
  return render(<QueryClientProvider client={new QueryClient()}>
    <TuyaPanel property={property} />
  </QueryClientProvider>)
}

it('shows the provisioning boundary when the project is unconfigured', async () => {
  const status = vi.spyOn(api, 'tuya').mockResolvedValue({
    status: 'notConfigured', manage: false, lastSuccess: null, nextSync: null, providerCode: null, devices: [], unassigned: [],
  })
  try {
    view()
    expect(await screen.findByText(/linked Smart Life account are required/)).toBeInTheDocument()
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  } finally { status.mockRestore() }
})

it('renders scaled, stale and undated readings as read-only values', async () => {
  const receipt = '2026-11-22T12:00:00Z'
  const status = vi.spyOn(api, 'tuya').mockResolvedValue({
    status: 'connected', manage: false, lastSuccess: receipt, nextSync: null, providerCode: null, unassigned: [],
    devices: [{ id: 'fixture', name: 'Test monitor', model: 'Fixture', roomId: 1, connectivity: 'online',
      receivedAt: receipt, quality: 'fresh', specificationsAvailable: true, readings: [
        { code: 'temp_current', metric: 'temperature', unit: '°C', number: 21.5, flag: null, reportedAt: receipt, quality: 'stale' },
        { code: 'battery_percentage', metric: 'batterySoc', unit: '%', number: 99, flag: null, reportedAt: null, quality: 'missing' },
        { code: 'switch_1', metric: 'switchOn', unit: null, number: null, flag: false, reportedAt: receipt, quality: 'fresh' },
      ] }],
  })
  try {
    view()
    expect(await screen.findByRole('heading', { name: 'Test monitor' })).toBeInTheDocument()
    expect(screen.getByText(/21[.,]5 °C · Last known/)).toBeInTheDocument()
    expect(screen.getByText('99% · Report time unavailable')).toBeInTheDocument()
    expect(screen.getByText('Off')).toBeInTheDocument()
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  } finally { status.mockRestore() }
})
