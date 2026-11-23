import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { expect, it, vi } from 'vitest'
import { api, ApiError } from '../shared/api'
import { EwelinkPanel } from './EwelinkPanel'

const property = { id: 1, name: 'Fixture', timeZone: 'Europe/Sofia' }

it('submits presence only while visible and distinguishes acceptance from live measurements', async () => {
  const time = '2026-11-23T08:00:00Z'
  const status = vi.spyOn(api, 'ewelink').mockResolvedValue({
    status: 'connected', manage: false, lastSuccess: time, nextSync: null, unassigned: [],
    canViewLiveElectrical: true,
    devices: [{ id: 'relay', name: 'Boiler', model: 'PSF-X67', roomId: 1, connectivity: 'online',
      seenAt: time, quality: 'fresh', hasRelay: true, relay: false, relayAt: time, relayQuality: 'fresh',
      electricalReporting: { status: 'awaitingReport' }, measurements: [
        { metric: 'activePower', unit: 'W', observedAt: time, number: 0, flag: null, code: null, quality: 'fresh' },
      ] }],
  })
  const send = vi.fn(async (_active: boolean) => {})
  const prepare = vi.spyOn(api, 'prepareEwelinkElectricalViewer').mockResolvedValue(send)
  const visibility = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible')
  try {
    const page = view()
    expect(await screen.findByText('Electrical reporting accepted. Waiting for a measurement change.')).toBeInTheDocument()
    expect(screen.getByText('Active power').nextElementSibling).toHaveTextContent(/0 W.*Last known/)
    await waitFor(() => expect(send.mock.calls).toEqual([[true]]))
    visibility.mockReturnValue('hidden')
    fireEvent(document, new Event('visibilitychange'))
    await waitFor(() => expect(send.mock.calls).toEqual([[true], [false]]))
    visibility.mockReturnValue('visible')
    fireEvent(document, new Event('visibilitychange'))
    await waitFor(() => expect(send.mock.calls).toEqual([[true], [false], [true]]))
    page.unmount()
    await waitFor(() => expect(send.mock.calls).toEqual([[true], [false], [true], [false]]))
    expect(prepare).toHaveBeenCalledExactlyOnceWith(1, expect.any(String))
  } finally {
    status.mockRestore()
    prepare.mockRestore()
    visibility.mockRestore()
  }
})

it('distinguishes live measurements from last-known values and retains their timestamps', async () => {
  const time = '2026-11-23T08:00:00Z'
  const status = vi.spyOn(api, 'ewelink').mockResolvedValue({
    status: 'connected', manage: false, lastSuccess: time, nextSync: null, unassigned: [],
    devices: [{ id: 'relay', name: 'Boiler', model: 'PSF-X67', roomId: 1, connectivity: 'online',
      seenAt: time, quality: 'fresh', hasRelay: true, relay: false, relayAt: time, relayQuality: 'fresh',
      socketMetrics: ['activePower', 'voltage'], measurements: [
        { metric: 'activePower', unit: 'W', observedAt: time, number: 54.2, flag: null, code: null, quality: 'fresh' },
        { metric: 'voltage', unit: 'V', observedAt: time, number: 230, flag: null, code: null, quality: 'stale' },
      ] }],
  })
  const trace = vi.spyOn(console, 'info').mockImplementation(() => {})
  try {
    view('/?ewelinkDiagnostics=1')
    expect((await screen.findByText('Active power')).nextElementSibling).toHaveTextContent(/54.2.*Live/)
    expect(screen.getByText('Voltage').nextElementSibling).toHaveTextContent(/230.*Last known/)
    expect(screen.getByText('Active power').nextElementSibling?.querySelector('time')).toHaveAttribute('dateTime', time)
    expect(trace).toHaveBeenCalledExactlyOnceWith('eWeLink React trace', {
      cards: 1, electricalRows: 2, liveRows: 1, waitingDevices: 0,
    })
  } finally {
    status.mockRestore()
    trace.mockRestore()
  }
})

it('explains per-device opt-in and reveals commands only after the preference is saved', async () => {
  const time = '2026-11-23T08:00:00Z'
  let enabled = false
  const status = vi.spyOn(api, 'ewelink').mockImplementation(async () => ({
    status: 'connected', manage: false, lastSuccess: time, nextSync: null, unassigned: [],
    devices: [{ id: 'relay', name: 'Boiler', model: 'PSF-X67', roomId: 1, connectivity: 'online',
      seenAt: time, quality: 'fresh', hasRelay: true, relay: false, relayAt: time, relayQuality: 'fresh',
      canWriteRelay: enabled, relayControlStatus: enabled ? 'enabled' : 'deviceDisabled' }],
  }))
  const save = vi.spyOn(api, 'setEwelinkRelayWrites').mockImplementation(async (_, __, value) => { enabled = value })
  const actuator = vi.spyOn(api, 'commandEwelinkRelay')
  try {
    view()
    expect(await screen.findByText(/Relay writes are off for this device/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'ON' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Enable relay controls' }))
    expect(await screen.findByRole('button', { name: 'ON' })).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Disable relay controls' })).toBeEnabled()
    expect(save).toHaveBeenCalledExactlyOnceWith(1, 'relay', true)
    expect(actuator).not.toHaveBeenCalled()
  } finally {
    status.mockRestore()
    save.mockRestore()
    actuator.mockRestore()
  }
})

it('requires confirmation for each protected relay command and preserves reported state during submission', async () => {
  const time = '2026-11-23T08:00:00Z'
  const status = vi.spyOn(api, 'ewelink').mockResolvedValue({
    status: 'connected', manage: false, lastSuccess: time, nextSync: null, unassigned: [],
    devices: [{ id: 'relay', name: 'Inverter In', model: 'PSF-X67', roomId: 1, connectivity: 'online',
      seenAt: time, quality: 'fresh', hasRelay: true, relay: false, relayAt: time, relayQuality: 'fresh',
      canWriteRelay: true, requiresRelayConfirmation: true }],
  })
  const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true)
  type Result = Awaited<ReturnType<typeof api.commandEwelinkRelay>>
  let resolve!: (value: Result) => void
  const result = new Promise<Result>(complete => { resolve = complete })
  const command = vi.spyOn(api, 'commandEwelinkRelay').mockReturnValue(result)
  try {
    view()
    const on = await screen.findByRole('button', { name: 'ON' })
    fireEvent.click(on)
    expect(command).not.toHaveBeenCalled()
    fireEvent.click(on)
    expect(await screen.findByText('Pending')).toBeInTheDocument()
    expect(command).toHaveBeenCalledExactlyOnceWith(1, 'relay', true, true)
    expect(on).toBeDisabled()
    expect(screen.getByText('Relay').nextElementSibling).toHaveTextContent('Off')
    resolve({ id: 'audit', status: 'unverified', open: true })
    expect(await screen.findByText('Unverified')).toBeInTheDocument()
    expect(on).toBeDisabled()
  } finally {
    status.mockRestore()
    confirm.mockRestore()
    command.mockRestore()
  }
})

it.each([
  [null, 'succeeded'],
  [new ApiError(429, 'syncDeferred'), 'deferred'],
  [new ApiError(429, 'quotaExceeded'), 'rate-limited'],
  [new ApiError(503, 'unavailable'), 'failed'],
] as const)('reports Force Refresh as %s with the expected outcome %s', async (error, outcome) => {
  const status = vi.spyOn(api, 'ewelink').mockResolvedValue({
    status: 'connected', manage: true, lastSuccess: '2026-11-23T08:00:00Z', lastAttempt: '2026-11-23T08:10:00Z',
    nextSync: '2099-11-23T08:20:00Z', devices: [], unassigned: [],
  })
  const rooms = vi.spyOn(api, 'rooms').mockResolvedValue([])
  const refresh = vi.spyOn(api, 'updateEwelink').mockImplementation(async () => {
    if (error) throw error
  })
  try {
    view()
    const button = await screen.findByRole('button', { name: 'Force Refresh' })
    expect(button).toBeEnabled()
    expect(screen.getByText(/^Last attempt/)).toBeInTheDocument()
    expect(screen.getByText(/^Last successful sync/)).toBeInTheDocument()
    fireEvent.click(button)
    expect(await screen.findByText(new RegExp(`^Refresh ${outcome}`))).toBeInTheDocument()
    expect(refresh).toHaveBeenCalledExactlyOnceWith(1, 'refresh')
  } finally {
    status.mockRestore()
    rooms.mockRestore()
    refresh.mockRestore()
  }
})

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
