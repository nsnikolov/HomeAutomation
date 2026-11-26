import { useState, type ComponentProps } from 'react'
import { webcrypto } from 'node:crypto'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, LogicalApiError } from '../shared/api'
import type { DashboardGroup, DashboardSource, LogicalDeviceList, PhysicalDevice, ReplacementCandidate } from '../shared/types'
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

beforeEach(() => {
  vi.stubGlobal('crypto', webcrypto)
  vi.spyOn(api, 'physicalDevices').mockResolvedValue({ canManage: false, devices: [] })
  vi.spyOn(api, 'rooms').mockResolvedValue([{ id: 2, name: 'Plant room' }])
})
afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

function view() {
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <MemoryRouter><LogicalDevices property={property} /></MemoryRouter>
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

  it('links a logical device with recorded measurements to its history', async () => {
    const data = list(false)
    data.devices[0].metrics = [{ channelKey: 'main', metric: 'activePower', unit: 'W', aggregation: 'gauge', current: true }]
    vi.spyOn(api, 'logicalDevices').mockResolvedValue(data)
    view()
    expect(await screen.findByRole('link', { name: 'View history' })).toHaveAttribute('href', '/properties/3/history?device=4')
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
    // Hardware used by another logical device can be chosen; the form then explains what ends there (Milestone 7E).
    expect(within(select).getByRole('option', { name: /Busy meter .*in use by Boiler relay; ends there/ })).toBeEnabled()
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

describe('logical device management', () => {
  const managed = (): LogicalDeviceList => {
    const data = list(true)
    data.devices[0].creation = 'initialized'
    data.devices.push({ ...data.devices[0], id: 5, name: 'Loft thermometer', kind: 'Sensor', roomId: 2, current: [], previous: [],
      creation: 'manual', description: 'Spare probe' })
    data.devices.push({ ...data.devices[0], id: 6, name: 'Old boiler meter', archivedAt: '2026-11-20T10:00:00+00:00', current: [],
      creation: 'manual' })
    return data
  }
  const hardware: PhysicalDevice = {
    id: 9, name: 'Sonoff POWR3', provider: 'ewelink', manufacturer: 'Sonoff', model: 'POWR3', kind: 'SmartSwitch', roomId: null,
    capabilities: ['powerMeasurement'], endpoints: [], requiresEndpoint: false, logicalDeviceId: 12, logicalDeviceName: 'Sonoff POWR3',
    logicalCreation: 'automatic', everAssigned: true,
  }

  it('filters by room, kind, assignment and archive state and restores without reassigning hardware', async () => {
    vi.spyOn(api, 'logicalDevices').mockResolvedValue(managed())
    const restore = vi.spyOn(api, 'restoreLogicalDevice').mockResolvedValue({ id: 6 })
    view()

    expect(await screen.findByRole('heading', { name: 'Heat pump power meter' })).toBeInTheDocument()
    expect(screen.getByText('Initialized')).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Old boiler meter' })).not.toBeInTheDocument()
    const filters = screen.getByRole('group', { name: 'Filter logical devices' })
    fireEvent.change(await within(filters).findByLabelText('Room'), { target: { value: '2' } })
    expect(screen.queryByRole('heading', { name: 'Heat pump power meter' })).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Loft thermometer' })).toBeInTheDocument()
    expect(screen.getByText(/No physical device is currently assigned, so no current readings are shown/)).toBeInTheDocument()
    fireEvent.change(within(filters).getByLabelText('Room'), { target: { value: '' } })
    fireEvent.change(within(filters).getByLabelText('Assignment'), { target: { value: 'assigned' } })
    expect(screen.queryByRole('heading', { name: 'Loft thermometer' })).not.toBeInTheDocument()
    fireEvent.change(within(filters).getByLabelText('Assignment'), { target: { value: 'all' } })
    fireEvent.change(within(filters).getByLabelText('Kind'), { target: { value: 'Sensor' } })
    expect(screen.queryByRole('heading', { name: 'Heat pump power meter' })).not.toBeInTheDocument()
    fireEvent.change(within(filters).getByLabelText('Kind'), { target: { value: '' } })
    fireEvent.change(within(filters).getByLabelText('Status'), { target: { value: 'archived' } })
    const archived = await screen.findByRole('heading', { name: 'Old boiler meter' })
    expect(screen.getByText(/keep their history but record no new readings/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Archive' })).not.toBeInTheDocument()
    fireEvent.click(within(archived.closest('.detail-card') as HTMLElement).getByRole('button', { name: 'Restore' }))
    await waitFor(() => expect(restore).toHaveBeenCalledWith(3, 6, null))
    expect(await screen.findByText(/active again. Assign a device to record new readings/)).toBeInTheDocument()
  })

  it('creates a logical device without hardware and edits details with the loaded revision', async () => {
    vi.spyOn(api, 'logicalDevices').mockResolvedValue(managed())
    const create = vi.spyOn(api, 'createLogicalDevice').mockResolvedValue({ id: 7 })
    const update = vi.spyOn(api, 'updateLogicalDevice').mockRejectedValue(
      new LogicalApiError(409, 'conflict', 'Someone else changed this logical device since it was loaded. Reload and try again.'))
    view()

    fireEvent.click(await screen.findByRole('button', { name: 'Create logical device' }))
    const form = screen.getByRole('form', { name: 'Create a logical device' })
    fireEvent.change(within(form).getByLabelText('Name'), { target: { value: 'Garage meter' } })
    fireEvent.change(within(form).getByLabelText('Kind'), { target: { value: 'SmartSwitch' } })
    fireEvent.change(await within(form).findByLabelText('Room'), { target: { value: '2' } })
    expect(within(form).getByLabelText('Physical device (optional)')).toHaveValue('')
    fireEvent.click(within(form).getByRole('button', { name: 'Create' }))
    await waitFor(() => expect(create).toHaveBeenCalledWith(3, {
      name: 'Garage meter', kind: 'SmartSwitch', roomId: 2, description: null, deviceId: null, endpointId: null,
    }))
    expect(await screen.findByText(/created without hardware/)).toBeInTheDocument()

    const card = screen.getByRole('heading', { name: 'Loft thermometer' }).closest('.detail-card') as HTMLElement
    fireEvent.click(within(card).getByRole('button', { name: 'Edit details' }))
    const edit = within(card).getByRole('form', { name: 'Edit Loft thermometer' })
    expect(within(edit).getByLabelText('Description (optional)')).toHaveValue('Spare probe')
    fireEvent.change(within(edit).getByLabelText('Name'), { target: { value: 'Attic temperature' } })
    fireEvent.click(within(edit).getByRole('button', { name: 'Save changes' }))
    await waitFor(() => expect(update).toHaveBeenCalledWith(3, 5, {
      name: 'Attic temperature', kind: 'Sensor', roomId: 2, description: 'Spare probe', expectedUpdatedAt: assignment.assignedAt,
    }))
    expect(await within(edit).findByRole('alert')).toHaveTextContent('Someone else changed this logical device')
  })

  it('resolves an auto-created identity as a confirmed replacement that keeps its earlier readings', async () => {
    vi.spyOn(api, 'logicalDevices').mockResolvedValue(managed())
    vi.spyOn(api, 'physicalDevices').mockResolvedValue({ canManage: true, devices: [hardware] })
    vi.spyOn(api, 'replacementCandidates').mockResolvedValue([candidate({ assignedLogicalDeviceId: 12,
      assignedLogicalDeviceName: 'Sonoff POWR3', assignedLogicalCreation: 'automatic', missing: [] })])
    const preview = vi.spyOn(api, 'releasePreview').mockResolvedValue({
      logicalDeviceId: 12, name: 'Sonoff POWR3', creation: 'automatic', channels: ['main'], readings: 14,
      firstReading: '2026-11-26T12:00:00Z', lastReading: '2026-11-26T12:30:00Z', leavesUnassigned: true, hasOtherHardwareHistory: false,
      dashboardReferences: 1,
    })
    const replace = vi.spyOn(api, 'replaceDevice').mockResolvedValue({ id: 4 })
    view()

    fireEvent.click(await screen.findByRole('tab', { name: 'Unassigned and new hardware (1)' }))
    expect(await screen.findByText(/Recording into "Sonoff POWR3", created automatically/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Use as replacement for an existing logical device' }))
    const choose = screen.getByRole('form', { name: 'Use Sonoff POWR3 for an existing logical device' })
    fireEvent.change(within(choose).getByLabelText('Logical device'), { target: { value: '4' } })
    fireEvent.click(within(choose).getByRole('button', { name: 'Continue' }))

    const form = await screen.findByRole('form', { name: 'Replace the device of Heat pump power meter' })
    expect(await within(form).findByRole('combobox', { name: 'Replacement device' })).toHaveValue('9')
    fireEvent.change(within(form).getByLabelText('Replacement time'), { target: { value: '2026-11-26T14:37' } })
    const effect = await within(form).findByRole('region', { name: 'Effect on Sonoff POWR3' })
    expect(within(effect).getByText(/created automatically when the hardware was discovered/)).toBeInTheDocument()
    expect(within(effect).getByRole('alert')).toHaveTextContent(/14 readings .* stay with Sonoff POWR3/)
    expect(within(effect).getByText(/1 dashboard measurement uses Sonoff POWR3. They are not changed/)).toBeInTheDocument()
    expect(within(effect).getByLabelText(/Archive Sonoff POWR3 afterwards/)).toBeChecked()
    await waitFor(() => expect(preview).toHaveBeenCalledWith(3, 4, 9, null, new Date('2026-11-26T14:37').toISOString(), expect.anything()))
    fireEvent.click(within(form).getByLabelText(/I confirm that Sonoff POWR3 replaced Tongou PSF-X67/))
    const save = within(form).getByRole('button', { name: 'Save replacement' })
    expect(save).toBeDisabled()
    fireEvent.click(within(effect).getByLabelText(/Keep those readings with Sonoff POWR3/))
    expect(save).toBeEnabled()
    fireEvent.click(save)

    await waitFor(() => expect(replace).toHaveBeenCalledWith(3, 4, {
      channelKey: 'main', expectedAssignmentId: 21, deviceId: 9, endpointId: null,
      effectiveAt: new Date('2026-11-26T14:37').toISOString(), reason: null, acceptMissingCapabilities: false,
      releaseFrom: 12, acceptReleasedHistory: true, archiveReleased: true,
    }))
  })

  it('assigns hardware to an empty logical device and unassigns with an explicit time', async () => {
    vi.spyOn(api, 'logicalDevices').mockResolvedValue(managed())
    vi.spyOn(api, 'replacementCandidates').mockResolvedValue([candidate({ deviceId: 30, name: 'Tuya probe', capabilities: ['temperatureMeasurement'] })])
    const assign = vi.spyOn(api, 'assignDevice').mockResolvedValue({ id: 5 })
    const unassign = vi.spyOn(api, 'unassignDevice').mockResolvedValue({ id: 4 })
    view()

    const empty = (await screen.findByRole('heading', { name: 'Loft thermometer' })).closest('.detail-card') as HTMLElement
    fireEvent.click(within(empty).getByRole('button', { name: 'Assign device' }))
    const form = within(empty).getByRole('form', { name: 'Assign a device to Loft thermometer' })
    fireEvent.change(await within(form).findByRole('combobox', { name: 'Physical device' }), { target: { value: '30' } })
    fireEvent.change(within(form).getByLabelText('Start time'), { target: { value: '2026-11-25T09:00' } })
    fireEvent.click(within(form).getByLabelText(/I confirm that Tuya probe belongs to Loft thermometer/))
    fireEvent.click(within(form).getByRole('button', { name: 'Save assignment' }))
    await waitFor(() => expect(assign).toHaveBeenCalledWith(3, 5, {
      channelKey: 'main', deviceId: 30, endpointId: null, effectiveAt: new Date('2026-11-25T09:00').toISOString(), reason: null,
    }))

    const meter = screen.getByRole('heading', { name: 'Heat pump power meter' }).closest('.detail-card') as HTMLElement
    fireEvent.click(within(meter).getByRole('button', { name: 'Unassign' }))
    const end = within(meter).getByRole('form', { name: 'Unassign Tongou PSF-X67 from Heat pump power meter' })
    fireEvent.change(within(end).getByLabelText('End time'), { target: { value: '2026-11-26T08:15' } })
    const submit = within(end).getByRole('button', { name: 'Unassign' })
    expect(submit).toBeDisabled()
    fireEvent.click(within(end).getByLabelText(/stopped serving Heat pump power meter/))
    fireEvent.click(submit)
    await waitFor(() => expect(unassign).toHaveBeenCalledWith(3, 4, {
      channelKey: 'main', expectedAssignmentId: 21, effectiveAt: new Date('2026-11-26T08:15').toISOString(), reason: null,
    }))
  })

  it('archives only after confirmation', async () => {
    vi.spyOn(api, 'logicalDevices').mockResolvedValue(managed())
    const archive = vi.spyOn(api, 'archiveLogicalDevice').mockResolvedValue({ id: 5 })
    view()
    const card = (await screen.findByRole('heading', { name: 'Loft thermometer' })).closest('.detail-card') as HTMLElement
    fireEvent.click(within(card).getByRole('button', { name: 'Archive' }))
    const form = within(card).getByRole('form', { name: 'Archive Loft thermometer' })
    const submit = within(form).getByRole('button', { name: 'Archive' })
    expect(submit).toBeDisabled()
    fireEvent.click(within(form).getByLabelText('Archive Loft thermometer'))
    fireEvent.click(submit)
    await waitFor(() => expect(archive).toHaveBeenCalledWith(3, 5, null))
    expect(await screen.findByText(/was archived. Its history stays available/)).toBeInTheDocument()
  })

  it('shows hardware to viewers without management actions', async () => {
    vi.spyOn(api, 'logicalDevices').mockResolvedValue({ ...managed(), canManage: false })
    vi.spyOn(api, 'physicalDevices').mockResolvedValue({ canManage: false, devices: [{ ...hardware, logicalDeviceId: null,
      logicalDeviceName: null, logicalCreation: null, everAssigned: false }] })
    view()
    expect(await screen.findByRole('heading', { name: 'Heat pump power meter' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Create logical device' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Edit details' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Assign device' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('tab', { name: /Unassigned and new hardware/ }))
    expect(await screen.findByText('Not assigned to a logical device.')).toBeInTheDocument()
    expect(screen.getByText(/not necessarily offline or faulty/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Create logical device|Assign to an existing/ })).not.toBeInTheDocument()
  })

  it('reports a hardware loading failure without hiding logical devices', async () => {
    vi.spyOn(api, 'logicalDevices').mockResolvedValue(managed())
    vi.spyOn(api, 'physicalDevices').mockRejectedValue(new LogicalApiError(500, undefined, 'Failed'))
    view()
    expect(await screen.findByRole('heading', { name: 'Heat pump power meter' })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('tab', { name: /Unassigned and new hardware/ }))
    expect(await screen.findByText('Hardware could not be loaded.')).toBeInTheDocument()
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
