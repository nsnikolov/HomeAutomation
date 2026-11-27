// Growatt availability, PV display and installation summary, and Devices search across every section (prompt 0068).
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../shared/api'
import type { DashboardGroup, DashboardSource, Device, InstallationValue, LogicalAssignment, LogicalDevice, Reading } from '../shared/types'
import { displayNumber } from '../shared/utils'
import { DashboardGroups } from './DashboardWidgets'
import { DeviceBadge, Devices, formatReading } from './Devices'
import { GrowattSummary, InstallationReading } from './GrowattPanel'
import { defaultWidgetView } from './dashboardWidgetViews'

const at = '2026-11-27T21:00:00Z'
const reading = (metric: Reading['metric'], unit: string | null, number: number | null, quality: Reading['quality'] = 'fresh'): Reading =>
  ({ metric, unit, observedAt: at, number, flag: null, code: null, quality })
const base: Device = {
  id: 1, roomId: null, name: 'Device', kind: 'sensor', manufacturer: 'Fixture', model: 'Model', connectivity: 'online', faultCode: null,
  lastContactAt: null, quality: 'fresh', endpoints: [],
}

describe('Growatt availability and PV display', () => {
  it('shows recent evidence as Online and keeps Growatt’s own offline flag as a diagnostic', () => {
    render(<DeviceBadge device={{ ...base, availability: 'online', reportedConnectivity: 'offline', availabilityBasis: 'recentReading',
      availabilityWindowSeconds: 450 }} />)
    expect(screen.getByText('Online')).toHaveAttribute('title', 'A reading arrived within the last 7 min 30 s. The provider reports it as offline.')
  })

  it('shows Stale, not Offline, when the availability window expires, and Offline only for a confirmed disconnection', () => {
    render(<>
      <DeviceBadge device={{ ...base, connectivity: 'unknown', availability: 'stale', availabilityBasis: 'noRecentEvidence', availabilityWindowSeconds: 900 }} />
      <DeviceBadge device={{ ...base, connectivity: 'offline', availability: 'offline', availabilityBasis: 'providerOffline' }} />
      <DeviceBadge device={{ ...base, connectivity: 'unknown', availability: 'unknown', availabilityBasis: 'noEvidence' }} />
    </>)
    expect(screen.getByText('Stale')).toHaveAttribute('title', 'No reading or status within the last 15 min. This is not a confirmed disconnection.')
    expect(screen.getByText('Offline')).toHaveAttribute('title', 'The provider reported a disconnection, and no newer reading has arrived.')
    expect(screen.getByText('Unknown')).toHaveAttribute('title', 'No reading or status has been received yet.')
  })

  it('displays PV power from 0 to 10 W as 0 W, never shows negative PV as production and leaves other metrics as reported', () => {
    expect([0, 1, 5, 10, 11, 15, 20, 21, 100].map(watts => displayNumber('pvPower', watts))).toEqual([0, 0, 0, 0, 11, 15, 20, 21, 100])
    expect(displayNumber('pvPower', -3)).toBeNull()
    expect(formatReading(reading('pvPower', 'W', 1))).toBe('0 W')
    expect(formatReading(reading('pvPower', 'W', 11))).toBe('11 W')
    expect(formatReading(reading('pvPower', 'W', -3))).toBe('Invalid')
    expect(formatReading(reading('activePower', 'W', 1))).toBe('1 W')
    expect(formatReading(reading('pvPower', 'W', null, 'missing'))).toBe('—')
  })

  it('uses the server’s PV generation state per inverter and explains a 1 W night reading shown as 0 W', () => {
    const inverter = (id: number, name: string, measurements: Reading[], pv: Device['pvInputs']): Device => ({
      ...base, id, name, kind: 'inverter', availability: 'online', reportedConnectivity: 'offline', availabilityBasis: 'recentReading',
      availabilityWindowSeconds: 450, endpoints: [{ id, name: 'Main', capabilities: [], measurements }], pvInputs: pv,
    })
    render(<GrowattSummary timeZone="Europe/Sofia" result={{ status: 'running', lastSuccess: at, data: { installation: [], alerts: [], solar: [], devices: [
      inverter(1, 'Growatt master', [reading('pv1Voltage', 'V', 0), reading('pvPower', 'W', 1)],
        [{ endpointId: 1, state: 'notGenerating', reason: 'noVoltage', rawWatts: 1, displayWatts: 0 }]),
      inverter(2, 'Growatt slave', [reading('pvVoltage', 'V', 96), reading('pvPower', 'W', 15)],
        [{ endpointId: 2, state: 'generating', reason: 'heldBetweenThresholds', rawWatts: 15, displayWatts: 15 }]),
    ] } }} />)
    const master = within(screen.getByRole('region', { name: 'Growatt master' }))
    const slave = within(screen.getByRole('region', { name: 'Growatt slave' }))
    expect(master.getByText('No PV generation (0 V)')).toBeInTheDocument()
    expect(master.queryByText('PV generating')).not.toBeInTheDocument()
    const power = master.getByText('PV input power').nextElementSibling!
    expect(power).toHaveTextContent(/^0 W$/)
    expect(power).toHaveAttribute('title', expect.stringContaining('Raw reading 1 W'))
    expect(slave.getByText('PV generating')).toBeInTheDocument()
    expect(slave.getByText('PV input power').nextElementSibling).toHaveTextContent('15 W')
    expect(master.getByText('Online')).toBeInTheDocument()
    expect(master.getByText('Online from recent readings. Growatt itself reports this inverter as offline.')).toBeInTheDocument()
  })

  it('applies the same thresholds without server state: no generation at 0 V or up to 20 W, stale stays stale', () => {
    render(<GrowattSummary timeZone="Europe/Sofia" result={{ status: 'running', lastSuccess: at, data: { installation: [], alerts: [], devices: [
      { ...base, id: 1, name: 'Growatt master', kind: 'inverter', endpoints: [{ id: 1, name: 'Main', capabilities: [],
        measurements: [reading('pvVoltage', 'V', 0), reading('pvPower', 'W', 1)] }] },
      { ...base, id: 2, name: 'Growatt slave', kind: 'inverter', endpoints: [{ id: 2, name: 'Main', capabilities: [],
        measurements: [reading('pvVoltage', 'V', 90, 'stale'), reading('pvPower', 'W', 300, 'stale')] }] },
    ] } }} />)
    expect(within(screen.getByRole('region', { name: 'Growatt master' })).getByText('No PV generation (0 V)')).toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: 'Growatt slave' })).getByText('PV readings stale')).toBeInTheDocument()
    expect(screen.queryByText('PV generating')).not.toBeInTheDocument()
  })

  it('normalizes PV power on dashboard widgets too, without changing other values', () => {
    const source = (key: string, metric: DashboardSource['metric'], number: number): DashboardSource => ({
      key, provider: 'growatt', deviceId: '1', deviceName: 'Growatt master', channel: 'Main', metric, unit: 'W', route: '/properties/1/growatt',
      status: null, reading: { number, flag: null, code: null, observedAt: at, provenance: 'provider' },
    })
    const pv = source('growatt:1:PvPower', 'pvPower', 4)
    const load = source('growatt:1:ActivePower', 'activePower', 4)
    const groups: DashboardGroup[] = [{ id: 'g', title: 'Solar', hidden: false, collapsed: false, widgets: [{
      id: 'w', title: 'Growatt', mode: 'detailed', navigationSource: null,
      values: [{ id: 'p', sourceKey: pv.key, label: 'PV now' }, { id: 'l', sourceKey: load.key, label: 'Load now' }],
    }] }]
    render(<MemoryRouter><DashboardGroups groups={groups} sources={new Map([pv, load].map(item => [item.key, item]))}
      property={{ id: 1, name: 'House', timeZone: 'UTC' }} now={Date.parse(at)} views={new Map([['w', defaultWidgetView]])}
      onViewChange={() => undefined} /></MemoryRouter>)
    expect(screen.getByRole('link', { name: /PV now: 0 W/ })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Load now: 4 W/ })).toBeInTheDocument()
  })
})

describe('Solar installation summary', () => {
  const value = (patch: Partial<InstallationValue>): InstallationValue => ({
    slot: 'pvPower', basis: 'inverters', state: 'available', number: 0, unit: 'W', text: null, observedAt: at, used: 2, expected: 2,
    reason: null, ...patch,
  })

  it('labels derived values by what they measure and tells a valid zero from partial, stale and unavailable values', () => {
    render(<GrowattSummary timeZone="Europe/Sofia" result={{ status: 'running', lastSuccess: at, data: { installation: [], alerts: [], devices: [],
      solar: [
        value({ slot: 'pvPower', number: 950 }),
        value({ slot: 'consumption', state: 'partial', number: 300, used: 1, reason: 'Only Growatt master reported a current value; Growatt slave did not.' }),
        value({ slot: 'gridSupply', number: 244.5, unit: 'V', text: 'present' }),
        value({ slot: 'acInput', number: 0 }),
      ] } }} />)
    const summary = screen.getByText('Solar installation').nextElementSibling as HTMLElement
    const cell = (label: string) => within(summary).getByText(label).nextElementSibling
    expect(cell('PV production')).toHaveTextContent(/^950 W$/)
    expect(cell('Inverter output')).toHaveTextContent('300 W (partial)Only Growatt master reported a current value; Growatt slave did not.')
    expect(cell('AC input supply')).toHaveTextContent(/^Present · 244.5 V$/)
    expect(cell('AC input power')).toHaveTextContent(/^0 W$/)
    expect(within(summary).getByText('AC input supply')).toHaveAttribute('title', expect.stringContaining('not which source feeds it'))
    expect(within(summary).queryByText('Unavailable')).not.toBeInTheDocument()
    expect(within(summary).queryByText('Household consumption')).not.toBeInTheDocument()
  })

  it('shows stale, absent, unknown and unavailable values with their reasons, and provider totals under their own labels', () => {
    const { rerender } = render(<dl><dd>
      <InstallationReading value={value({ state: 'stale', number: 550 })} /></dd><dd>
      <InstallationReading value={value({ slot: 'gridSupply', text: 'absent', number: 0, unit: 'V' })} /></dd><dd>
      <InstallationReading value={value({ slot: 'gridSupply', state: 'stale', text: 'unknown', number: null, unit: 'V', reason: 'Old.' })} /></dd><dd>
      <InstallationReading value={value({ state: 'unavailable', number: null, reason: 'No inverter has reported this measurement.' })} /></dd>
    </dl>)
    expect(screen.getByText(/550 W \(stale\)/)).toBeInTheDocument()
    expect(screen.getByText(/^Absent · 0 V$/)).toBeInTheDocument()
    expect(screen.getByText(/^Unknown \(stale\)/)).toBeInTheDocument()
    expect(screen.getByText('No inverter has reported this measurement.')).toBeInTheDocument()
    rerender(<GrowattSummary timeZone="UTC" result={{ status: 'running', lastSuccess: at, data: { installation: [], alerts: [], devices: [],
      solar: [value({ slot: 'consumption', basis: 'installation', number: 700 })] } }} />)
    expect(screen.getByText('Household consumption').nextElementSibling).toHaveTextContent('700 W')
    expect(screen.getByText('PV production').nextElementSibling).toHaveTextContent('Unavailable')
  })
})

describe('Devices search', () => {
  const physical: Device[] = [
    { ...base, id: 11, name: 'Boiler Sturgel', roomId: 2, provider: 'tuya', manufacturer: 'Tuya', model: 'TH sensor' },
    { ...base, id: 12, name: 'Kitchen cooker', provider: 'ewelink', manufacturer: 'Sonoff', model: 'POWR3', kind: 'smartSwitch' },
    { ...base, id: 13, name: 'Heatpump', provider: 'daikin', manufacturer: 'Daikin', model: 'Altherma', kind: 'heatPump' },
    { ...base, id: 14, name: 'Heatpump', providerName: 'Heatpump Switch', customName: true, provider: 'ewelink', manufacturer: 'Tongou',
      model: 'PSF-X67', kind: 'smartSwitch' },
  ]
  const assignment = (deviceId: number, deviceName: string): LogicalAssignment => ({
    id: deviceId * 10, channelKey: 'main', deviceId, deviceName, provider: 'ewelink', manufacturer: 'Sonoff', model: 'POWR3', endpointId: null,
    endpointName: null, validFrom: at, validTo: null, assignedBy: 'user', assignedAt: at, reason: null, capabilities: [],
  })
  const logical = (id: number, name: string, deviceId: number, deviceName: string): LogicalDevice => ({
    id, name, kind: 'SmartSwitch', roomId: null, description: null, createdAt: at, updatedAt: at, archivedAt: null,
    current: [assignment(deviceId, deviceName)], previous: [],
  })
  let logicalDevices: LogicalDevice[]

  beforeEach(() => {
    logicalDevices = [logical(1, 'Boiler Sturgel', 11, 'Boiler Sturgel'), logical(2, 'Cooker meter', 12, 'Kitchen cooker')]
    vi.spyOn(api, 'rooms').mockResolvedValue([{ id: 2, name: 'Plant room' }])
    vi.spyOn(api, 'devicePreferences').mockResolvedValue({ order: {}, hidden: [] } as never)
    vi.spyOn(api, 'propertyManagement').mockResolvedValue({ properties: [], canCreate: false } as never)
    vi.spyOn(api, 'physicalDevices').mockResolvedValue({ canManage: false, devices: [] })
    vi.spyOn(api, 'logicalDevices').mockImplementation(async () => ({ canManage: false, devices: logicalDevices }))
  })
  afterEach(() => vi.restoreAllMocks())

  function show(devices = physical, property = { id: 1, name: 'Countryside House', timeZone: 'Europe/Sofia' }) {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const view = render(<QueryClientProvider client={client}><MemoryRouter><Devices property={property} devices={devices} /></MemoryRouter></QueryClientProvider>)
    return { client, view }
  }
  const search = (text: string) => fireEvent.change(screen.getByRole('searchbox', { name: 'Search devices' }), { target: { value: text } })
  const logicalNames = () => within(screen.getByRole('region', { name: 'Logical devices' })).queryAllByRole('heading', { level: 3 })
    .map(heading => heading.textContent)
  // Provider device cards: links outside the logical device section (whose cards link to their history).
  const cards = () => screen.queryAllByRole('link').filter(link => !link.closest('[aria-labelledby="logical-devices-heading"]'))
    .map(link => link.textContent)

  it('filters logical and provider devices by “cook” while typing, and restores everything when cleared', async () => {
    show()
    await waitFor(() => expect(logicalNames()).toEqual(['Boiler Sturgel', 'Cooker meter']))
    search('c')
    search('co')
    search('cook')
    await waitFor(() => expect(logicalNames()).toEqual(['Cooker meter']))
    expect(cards()).toEqual(['Kitchen cooker'])
    expect(screen.queryByText('Boiler Sturgel')).not.toBeInTheDocument()
    // Filtering is local: typing did not reload anything.
    expect(api.logicalDevices).toHaveBeenCalledTimes(1)
    search('')
    await waitFor(() => expect(logicalNames()).toEqual(['Boiler Sturgel', 'Cooker meter']))
    expect(cards()).toHaveLength(4)
  })

  it('matches case-insensitively and trimmed, by room, provider, model, custom and provider names, and keeps equal names apart', async () => {
    show()
    await waitFor(() => expect(logicalNames()).toHaveLength(2))
    search('  BOILER ')
    await waitFor(() => expect(logicalNames()).toEqual(['Boiler Sturgel']))
    expect(cards()).toEqual(['Boiler Sturgel'])
    search('plant room')
    await waitFor(() => expect(cards()).toEqual(['Boiler Sturgel']))
    search('heatpump')
    await waitFor(() => expect(cards()).toEqual(['Heatpump', 'Heatpump']))
    expect(logicalNames()).toEqual([])
    search('heatpump switch')
    await waitFor(() => expect(cards()).toEqual(['Heatpump']))
    search('daikin altherma')
    await waitFor(() => expect(cards()).toEqual(['Heatpump']))
    search('powr3')
    await waitFor(() => expect(logicalNames()).toEqual(['Boiler Sturgel', 'Cooker meter']))
    expect(cards()).toEqual(['Kitchen cooker'])
    search('#14')
    await waitFor(() => expect(cards()).toEqual(['Heatpump']))
  })

  it('shows one clear empty state when nothing matches instead of showing every device', async () => {
    show()
    await waitFor(() => expect(logicalNames()).toHaveLength(2))
    search('zzz')
    expect(await screen.findByText('No matching devices for “zzz”.')).toBeInTheDocument()
    expect(screen.getByText('No logical devices match “zzz”.')).toBeInTheDocument()
    expect(logicalNames()).toEqual([])
    expect(cards()).toEqual([])
  })

  it('keeps the filter on refreshed data and on a different property, and handles an empty property', async () => {
    const { client, view } = show()
    await waitFor(() => expect(logicalNames()).toHaveLength(2))
    search('cook')
    await waitFor(() => expect(logicalNames()).toEqual(['Cooker meter']))
    // A live update (SignalR invalidates the property) brings a renamed device; the active query applies to the new data.
    logicalDevices = [...logicalDevices, logical(3, 'Cooktop', 15, 'Hob')]
    await client.invalidateQueries({ queryKey: ['property', 1] })
    await waitFor(() => expect(logicalNames()).toEqual(['Cooker meter', 'Cooktop']))

    view.rerender(<QueryClientProvider client={client}><MemoryRouter><Devices property={{ id: 2, name: 'Lake House', timeZone: 'UTC' }}
      devices={[{ ...base, id: 21, name: 'Lake cooker' }, { ...base, id: 22, name: 'Lake boiler' }]} /></MemoryRouter></QueryClientProvider>)
    await waitFor(() => expect(api.logicalDevices).toHaveBeenCalledWith(2, expect.anything()))
    await waitFor(() => expect(cards()).toEqual(['Lake cooker']))
    view.unmount()

    logicalDevices = []
    show([])
    expect(await screen.findByText('No devices in this property yet.')).toBeInTheDocument()
  })
})
