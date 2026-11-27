import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { api } from '../shared/api'
import { TuyaPanel } from './TuyaPanel'
import type { DevicePreferences, TuyaDevice } from '../shared/types'

const property = { id: 1, name: 'Fixture', timeZone: 'Europe/Sofia' }
const timestamp = '2026-11-22T12:00:00Z'

beforeEach(() => { vi.spyOn(api, 'devicePreferences').mockResolvedValue({ order: {}, hidden: [] }) })
afterEach(() => { vi.restoreAllMocks() })

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
    expect(screen.queryByRole('button', { name: 'Refresh discovery' })).not.toBeInTheDocument()
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
    expect(screen.queryByRole('button', { name: 'Off' })).not.toBeInTheDocument()
    const history = vi.spyOn(api, 'tuyaHistory').mockResolvedValue({ from: timestamp, to: timestamp, pollSeconds: 1800, series: [] })
    fireEvent.click(screen.getByRole('button', { name: 'Temperature and humidity history' }))
    await screen.findByText(/No timestamped temperature/)
    fireEvent.change(screen.getByRole('combobox', { name: 'History period' }), { target: { value: '30d' } })
    await waitFor(() => expect(history).toHaveBeenLastCalledWith(1, 'fixture', '30d', expect.anything()))
  } finally { status.mockRestore() }
})

it('shows assigned devices before unassigned and keeps a hidden card out until restored', async () => {
  let saved: DevicePreferences = { order: {}, hidden: [] }
  vi.mocked(api.devicePreferences).mockImplementation(async () => saved)
  const visibility = vi.spyOn(api, 'setTuyaVisibility').mockImplementation(async (_property, id, hidden) => {
    saved = { ...saved, hidden: hidden ? [id] : [] }
  })
  const device: TuyaDevice = {
    id: 'one', name: 'Assigned sensor', model: '', roomId: 12, connectivity: 'online',
    receivedAt: timestamp, quality: 'fresh', specificationsAvailable: true, readings: [],
  }
  const candidate = { ...device, id: 'two', name: 'Unassigned sensor', roomId: null }
  const state = { status: 'connected', manage: true, lastSuccess: timestamp, nextSync: null, providerCode: null,
    devices: [device], unassigned: [candidate] }
  const provider = vi.spyOn(api, 'tuya').mockResolvedValue(state)
  const first = view()
  const assigned = await screen.findByRole('heading', { name: 'Assigned devices' })
  const unassigned = await screen.findByRole('heading', { name: 'Unassigned devices' })
  expect(assigned.compareDocumentPosition(unassigned) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  fireEvent.click(await screen.findByRole('button', { name: 'Hide Assigned sensor' }))
  await waitFor(() => expect(visibility).toHaveBeenCalledWith(1, 'one', true))
  expect(screen.queryByRole('heading', { name: 'Assigned sensor' })).not.toBeInTheDocument()
  first.unmount()

  provider.mockResolvedValue({ ...state, devices: [{ ...device, name: 'Rediscovered sensor' }] })
  view()
  fireEvent.click(await screen.findByRole('button', { name: 'Show hidden (1)' }))
  const hiddenSection = screen.getByRole('heading', { name: 'Hidden devices' }).closest('section')!
  await within(hiddenSection).findByRole('heading', { name: 'Rediscovered sensor' })
  expect(within(hiddenSection).queryByRole('combobox')).not.toBeInTheDocument()
  expect(within(hiddenSection).queryByRole('button', { name: 'Assign here' })).not.toBeInTheDocument()
  fireEvent.click(within(hiddenSection).getByRole('button', { name: 'Restore Rediscovered sensor' }))
  await waitFor(() => expect(visibility).toHaveBeenLastCalledWith(1, 'one', false))
  expect(await screen.findByRole('heading', { name: 'Assigned devices' })).toBeInTheDocument()
  expect(screen.queryByRole('heading', { name: 'Hidden devices' })).not.toBeInTheDocument()
})

it('offers Assign here only on unassigned cards and no inline room controls', async () => {
  const device: TuyaDevice = {
    id: 'one', name: 'Assigned sensor', model: '', roomId: 12, connectivity: 'online',
    receivedAt: timestamp, quality: 'fresh', specificationsAvailable: true, readings: [],
  }
  const provider = vi.spyOn(api, 'tuya').mockResolvedValue({ status: 'connected', manage: true, lastSuccess: timestamp, nextSync: null,
    providerCode: null, devices: [device], unassigned: [{ ...device, id: 'two', name: 'Unassigned sensor', roomId: null }] })
  const rooms = vi.spyOn(api, 'rooms')
  const assign = vi.spyOn(api, 'assignTuya').mockResolvedValue(undefined)
  view()
  const button = await screen.findByRole('button', { name: 'Assign here' })
  expect(screen.getAllByRole('button', { name: 'Assign here' })).toHaveLength(1)
  expect(screen.getByRole('heading', { name: 'Unassigned sensor' }).closest('.device-card')).toContainElement(button)
  expect(screen.queryByRole('combobox')).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Save room' })).not.toBeInTheDocument()
  expect(rooms).not.toHaveBeenCalled()
  const reads = provider.mock.calls.length
  fireEvent.click(button)
  await waitFor(() => expect(assign).toHaveBeenCalledExactlyOnceWith(1, 'two', null))
  await waitFor(() => expect(provider.mock.calls.length).toBeGreaterThan(reads))
})

it('does not offer Assign here to a viewer', async () => {
  const device: TuyaDevice = {
    id: 'two', name: 'Unassigned sensor', model: '', roomId: null, connectivity: 'online',
    receivedAt: timestamp, quality: 'fresh', specificationsAvailable: true, readings: [],
  }
  vi.spyOn(api, 'tuya').mockResolvedValue({ status: 'connected', manage: false, lastSuccess: timestamp, nextSync: null,
    providerCode: null, devices: [], unassigned: [device] })
  view()
  expect(await screen.findByRole('heading', { name: 'Unassigned sensor' })).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Assign here' })).not.toBeInTheDocument()
})
