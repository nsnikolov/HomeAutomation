import { useState, type ComponentProps } from 'react'
import { webcrypto } from 'node:crypto'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, LogicalApiError } from '../shared/api'
import type { DashboardGroup, DashboardSource, LogicalDeviceList, ReplacementCandidate } from '../shared/types'
import { DashboardGroups } from './DashboardWidgets'
import { defaultWidgetView, type WidgetView } from './dashboardWidgetViews'
import { LogicalDevices } from './LogicalDevices'

const property = { id: 3, name: 'Fixture', timeZone: 'Europe/Sofia' }
const assignment = {
  id: 21, channelKey: 'main', deviceId: 7, deviceName: 'Tongou PSF-X67', provider: 'ewelink', manufacturer: 'Tongou', model: 'PSF-X67',
  endpointId: null, endpointName: null, validFrom: '1970-01-01T00:00:00+00:00', validTo: null, assignedBy: 'system:initialize',
  assignedAt: '2026-11-20T10:00:00+00:00', reason: null, capabilities: ['powerMeasurement', 'voltageMeasurement'],
}
const list = (canManage: boolean): LogicalDeviceList => ({
  canManage,
  devices: [{
    id: 4, name: 'Heat pump power meter', kind: 'SmartSwitch', roomId: null, description: null, createdAt: assignment.assignedAt,
    updatedAt: assignment.assignedAt, archivedAt: null, current: [assignment],
    previous: [{ ...assignment, id: 20, deviceId: 6, deviceName: 'Old meter', provider: 'tuya', validTo: '2026-11-01T08:00:00+00:00',
      reason: 'Burnt out' }],
  }],
})
const candidate = (overrides: Partial<ReplacementCandidate>): ReplacementCandidate => ({
  deviceId: 9, name: 'Sonoff POWR3', provider: 'ewelink', manufacturer: 'Sonoff', model: 'POWR3', kind: 'smartSwitch', roomId: null,
  endpoints: [{ id: 90, name: 'Main', capabilities: ['powerMeasurement', 'energyMeasurement'] }],
  capabilities: ['powerMeasurement', 'energyMeasurement'], shared: ['powerMeasurement'], missing: ['voltageMeasurement'],
  added: ['energyMeasurement'], compatible: true, requiresEndpoint: false, assignedLogicalDeviceId: null,
  assignedLogicalDeviceName: null, current: false, ...overrides,
})

beforeEach(() => { vi.stubGlobal('crypto', webcrypto) })
afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

function view() {
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <LogicalDevices property={property} />
  </QueryClientProvider>)
}

describe('logical devices', () => {
  it('shows the current and previous physical devices without management actions for viewers', async () => {
    vi.spyOn(api, 'logicalDevices').mockResolvedValue(list(false))
    view()

    expect(await screen.findByRole('heading', { name: 'Heat pump power meter' })).toBeInTheDocument()
    expect(screen.getByText('Tongou PSF-X67')).toBeInTheDocument()
    expect(screen.getByText(/since its first recorded reading/)).toBeInTheDocument()
    const capabilities = screen.getByRole('list', { name: 'Tongou PSF-X67 capabilities' })
    expect(within(capabilities).getByText('Power')).toBeInTheDocument()
    expect(within(capabilities).getByText('Voltage')).toBeInTheDocument()
    expect(screen.getByText('Previous devices (1)')).toBeInTheDocument()
    expect(screen.getByText(/Old meter · Smart Life \/ Tuya/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Replace device/ })).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Dashboard bindings' })).not.toBeInTheDocument()
  })

  it('explains the database upgrade when logical devices are not yet available', async () => {
    vi.spyOn(api, 'logicalDevices').mockRejectedValue(new LogicalApiError(503, undefined, 'Pending'))
    view()
    expect(await screen.findByText(/after the database upgrade/)).toBeInTheDocument()
  })

  it('requires an explicit confirmation and sends the exact replacement time', async () => {
    vi.spyOn(api, 'logicalDevices').mockResolvedValue(list(true))
    vi.spyOn(api, 'replacementCandidates').mockResolvedValue([
      candidate({}),
      candidate({ deviceId: 10, name: 'Busy meter', assignedLogicalDeviceId: 8, assignedLogicalDeviceName: 'Boiler relay' }),
      candidate({ deviceId: 11, name: 'Thermometer', compatible: false, shared: [] }),
      candidate({ deviceId: 7, name: 'Tongou PSF-X67', current: true }),
    ])
    const replace = vi.spyOn(api, 'replaceDevice').mockResolvedValue({ id: 4 })
    view()

    fireEvent.click(await screen.findByRole('button', { name: 'Replace device' }))
    const form = await screen.findByRole('form', { name: 'Replace the device of Heat pump power meter' })
    const select = await within(form).findByRole('combobox', { name: 'Replacement device' })
    expect(within(select).getByRole('option', { name: /Busy meter .*in use by Boiler relay/ })).toBeDisabled()
    expect(within(select).getByRole('option', { name: /Thermometer .*no matching measurement/ })).toBeDisabled()
    expect(within(select).queryByRole('option', { name: /Tongou PSF-X67/ })).not.toBeInTheDocument()
    fireEvent.change(select, { target: { value: '9' } })
    fireEvent.change(within(form).getByLabelText('Replacement time'), { target: { value: '2026-11-26T14:37' } })
    fireEvent.change(within(form).getByLabelText('Reason (optional)'), { target: { value: 'Relay failed' } })

    const differences = within(form).getByRole('region', { name: 'Capability differences' })
    expect(within(differences).getByText('Continues: Power.')).toBeInTheDocument()
    expect(within(differences).getByText(/New measurements.*Energy/)).toBeInTheDocument()
    expect(within(differences).getByRole('alert')).toHaveTextContent(/does not report Voltage/)
    expect(within(differences).getByText(/remain attributed to Tongou PSF-X67/)).toHaveTextContent(/nothing is sent/)
    const save = within(form).getByRole('button', { name: 'Save replacement' })
    expect(save).toBeDisabled()
    fireEvent.click(within(form).getByLabelText(/I confirm that Sonoff POWR3 replaced Tongou PSF-X67/))
    expect(save).toBeDisabled()
    fireEvent.click(within(form).getByLabelText('Continue without Voltage'))
    expect(save).toBeEnabled()
    fireEvent.click(save)

    await waitFor(() => expect(replace).toHaveBeenCalledWith(3, 4, {
      channelKey: 'main', expectedAssignmentId: 21, deviceId: 9, endpointId: null,
      effectiveAt: new Date('2026-11-26T14:37').toISOString(), reason: 'Relay failed', acceptMissingCapabilities: true,
    }))
    expect(await screen.findByText(/now uses Sonoff POWR3. Earlier readings stay with Tongou PSF-X67/)).toBeInTheDocument()
  })

  it('shows the server explanation when a replacement is rejected', async () => {
    vi.spyOn(api, 'logicalDevices').mockResolvedValue(list(true))
    vi.spyOn(api, 'replacementCandidates').mockResolvedValue([candidate({ missing: [] })])
    vi.spyOn(api, 'replaceDevice').mockRejectedValue(
      new LogicalApiError(409, 'conflict', 'The active device changed since this page was loaded. Reload and try again.'))
    view()

    fireEvent.click(await screen.findByRole('button', { name: 'Replace device' }))
    const form = await screen.findByRole('form', { name: 'Replace the device of Heat pump power meter' })
    fireEvent.change(await within(form).findByRole('combobox', { name: 'Replacement device' }), { target: { value: '9' } })
    fireEvent.click(within(form).getByLabelText(/I confirm/))
    fireEvent.click(within(form).getByRole('button', { name: 'Save replacement' }))

    expect(await within(form).findByRole('alert')).toHaveTextContent('The active device changed since this page was loaded')
  })

  it('converts only the dashboard bindings that were explicitly selected', async () => {
    vi.spyOn(api, 'logicalDevices').mockResolvedValue(list(true))
    vi.spyOn(api, 'logicalBindings').mockResolvedValue({
      revision: 'a'.repeat(64),
      proposals: [{ valueId: 'v1', widgetTitle: 'Heat pump', sourceKey: 'ewelink:abc:ActivePower', logicalKey: 'logical:4:main:ActivePower',
        logicalName: 'Heat pump power meter', metric: 'activePower' }],
      skipped: [{ valueId: 'v2', sourceKey: 'tuya:x:temp', reason: 'No active logical device uses this measurement.' }],
    })
    const convert = vi.spyOn(api, 'convertLogicalBindings').mockResolvedValue(undefined)
    view()

    fireEvent.click(await screen.findByRole('button', { name: 'Review dashboard bindings' }))
    const option = await screen.findByLabelText(/Heat pump: Active power → Heat pump power meter/)
    expect(screen.getByText('Not converted (1)')).toBeInTheDocument()
    const button = screen.getByRole('button', { name: 'Convert selected' })
    expect(button).toBeDisabled()
    fireEvent.click(option)
    fireEvent.click(button)

    await waitFor(() => expect(convert).toHaveBeenCalledWith(3, 'a'.repeat(64), ['v1']))
    expect(await screen.findByText('1 dashboard measurement now follow logical devices.')).toBeInTheDocument()
  })
})

describe('logical dashboard sources', () => {
  function Groups(props: Omit<ComponentProps<typeof DashboardGroups>, 'views' | 'onViewChange'>) {
    const [views, setViews] = useState(new Map<string, WidgetView>())
    return <DashboardGroups {...props} views={views} onViewChange={(id, change) =>
      setViews(current => new Map(current).set(id, { ...(current.get(id) ?? defaultWidgetView), ...change }))} />
  }

  it('shows the current device reading and an explicit unavailable state for missing capabilities', () => {
    const now = Date.parse('2026-11-26T15:00:00Z')
    const power: DashboardSource = {
      key: 'logical:4:main:ActivePower', provider: 'logical', deviceId: '4', deviceName: 'Heat pump power meter',
      channel: 'Sonoff POWR3', metric: 'activePower', unit: 'W', route: '/properties/3/ewelink', status: null, physicalProvider: 'ewelink',
      reading: { number: 900, flag: null, code: null, observedAt: '2026-11-26T14:58:00Z', provenance: 'provider' },
    }
    const voltage: DashboardSource = { ...power, key: 'logical:4:main:Voltage', metric: 'voltage', unit: 'V', status: 'unsupported',
      reading: null, route: '/properties/3/devices' }
    const groups: DashboardGroup[] = [{ id: 'g', title: 'Energy', hidden: false, collapsed: false, widgets: [{
      id: 'w', title: 'Heat pump', mode: 'detailed', navigationSource: null,
      values: [{ id: 'p', sourceKey: power.key, label: null }, { id: 'v', sourceKey: voltage.key, label: null }],
    }] }]
    render(<MemoryRouter><Groups groups={groups} sources={new Map([power, voltage].map(item => [item.key, item]))}
      property={property} now={now} /></MemoryRouter>)

    expect(screen.getByText('900 W')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Open Heat pump power meter for Active power: 900 W/ })).toHaveAttribute('href', '/properties/3/ewelink')
    expect(screen.getAllByText(/Heat pump power meter · Sonoff POWR3 · eWeLink/).length).toBeGreaterThan(0)
    expect(screen.getByText(/The current device does not report this measurement/)).toBeInTheDocument()
    expect(screen.getByText('—')).toBeInTheDocument()
  })
})
