import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import type { ReactNode } from 'react'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import { afterEach, expect, it, vi } from 'vitest'
import { api, LogicalApiError } from '../shared/api'
import { ConfirmDialog } from '../shared/ConfirmDialog'
import type { DaikinDevice, DaikinState, Device, PropertyOverview } from '../shared/types'
import { DaikinPanel } from './DaikinPanel'
import { DeviceActions, OtherPropertyDevices } from './DeviceManagement'
import { PropertyManagement, RoomManagement } from './PropertyManagement'

const property = { id: 1, name: 'Countryside House', timeZone: 'Europe/Sofia' }
const overview = (patch: Partial<PropertyOverview>): PropertyOverview => ({
  id: 1, name: 'Countryside House', description: null, timeZone: 'Europe/Sofia', role: 'Owner', canManage: true, canDelete: true,
  rooms: 0, devices: 0, installations: 0, growattProfile: null, ...patch,
})

function Location() {
  return <span data-testid="location">{useLocation().pathname}</span>
}

function show(node: ReactNode, path = '/properties/1/settings') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[path]}>
    <Routes><Route path="*" element={<>{node}<Location /></>} /></Routes>
  </MemoryRouter></QueryClientProvider>)
}

afterEach(() => vi.restoreAllMocks())

it('ConfirmDialog enables a typed confirmation only for the exact text and runs the action once', async () => {
  let finish: () => void = () => {}
  const confirm = vi.fn(() => new Promise<void>(resolve => { finish = resolve }))
  const cancel = vi.fn()
  render(<ConfirmDialog open title="Disconnect?" confirmLabel="Disconnect" destructive typedConfirmation="DISCONNECT"
    onConfirm={confirm} onCancel={cancel}><p>Consequences</p></ConfirmDialog>)
  const dialog = screen.getByRole('dialog', { name: 'Disconnect?' })
  const button = within(dialog).getByRole('button', { name: 'Disconnect' })
  expect(button).toBeDisabled()
  fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'disconnect' } })
  expect(button).toBeDisabled()
  fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'DISCONNECT' } })
  expect(button).toBeEnabled()
  fireEvent.click(button)
  fireEvent.click(button)
  fireEvent.keyDown(within(dialog).getByRole('textbox'), { key: 'Enter' })
  expect(confirm).toHaveBeenCalledTimes(1)
  expect(within(dialog).getByRole('button', { name: 'Working…' })).toBeDisabled()
  fireEvent.keyDown(dialog, { key: 'Escape' })
  expect(cancel).not.toHaveBeenCalled()
  await act(async () => finish())
  fireEvent.keyDown(dialog, { key: 'Escape' })
  expect(cancel).toHaveBeenCalledTimes(1)
})

it('ConfirmDialog keeps the dialog open and shows a failure', async () => {
  render(<ConfirmDialog open title="Delete room?" confirmLabel="Delete room" onCancel={() => {}}
    onConfirm={() => Promise.reject(new LogicalApiError(409, undefined, 'The room changed.'))} />)
  fireEvent.click(screen.getByRole('button', { name: 'Delete room' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('The room changed.')
  expect(screen.getByRole('dialog')).toBeInTheDocument()
})

it('creates a property with the default time zone and reuses the request id when the submission is retried', async () => {
  vi.spyOn(api, 'propertyManagement').mockResolvedValue({ canCreate: true, properties: [overview({})] })
  const create = vi.spyOn(api, 'createProperty')
    .mockRejectedValueOnce(new LogicalApiError(503, undefined, 'Apply migration 020 first.'))
    .mockResolvedValueOnce({ id: 9 })
  show(<PropertyManagement current={property} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Add property' }))
  const form = within(screen.getByRole('form', { name: 'Add property' }))
  expect(form.getByLabelText('Time zone')).toHaveValue('Europe/Sofia')
  fireEvent.change(form.getByLabelText('Name'), { target: { value: 'Lake House' } })
  fireEvent.click(form.getByRole('button', { name: 'Create property' }))
  expect(await form.findByRole('alert')).toHaveTextContent('Apply migration 020 first.')
  fireEvent.click(form.getByRole('button', { name: 'Create property' }))
  await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/properties/9/settings'))
  expect(create).toHaveBeenCalledTimes(2)
  const [first, second] = create.mock.calls.map(call => call[0])
  expect(first).toMatchObject({ name: 'Lake House', description: null, timeZone: 'Europe/Sofia' })
  expect(second.requestId).toBe(first.requestId)
})

it('does not offer creation or deletion to roles the server does not allow', async () => {
  vi.spyOn(api, 'propertyManagement').mockResolvedValue({ canCreate: false, properties: [
    overview({ role: 'Viewer', canManage: false, canDelete: false }),
  ] })
  show(<PropertyManagement current={property} />)
  expect(await screen.findByText('Viewer')).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Add property' })).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: /Actions for/ })).not.toBeInTheDocument()
})

it('blocks deletion while the impact review reports current devices', async () => {
  vi.spyOn(api, 'propertyManagement').mockResolvedValue({ canCreate: false, properties: [overview({ devices: 2 })] })
  vi.spyOn(api, 'deletionImpact').mockResolvedValue({ id: 1, name: 'Countryside House', rooms: 3, devices: 2, installations: 1,
    historicalDevices: 0, members: 2, vaultProfiles: 1, dashboardGroups: 0, canDelete: false })
  const remove = vi.spyOn(api, 'deleteProperty')
  show(<PropertyManagement current={property} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Actions for Countryside House' }))
  fireEvent.click(screen.getByRole('menuitem', { name: 'Delete property…' }))
  const dialog = within(await screen.findByRole('dialog', { name: 'Delete Countryside House?' }))
  expect(await dialog.findByText('2 devices (blocks deletion)')).toBeInTheDocument()
  fireEvent.change(dialog.getByRole('textbox'), { target: { value: 'Countryside House' } })
  expect(dialog.getByRole('button', { name: 'Delete property' })).toBeDisabled()
  expect(remove).not.toHaveBeenCalled()
})

it('deletes the current property after the exact name is typed and leaves it', async () => {
  const list = vi.spyOn(api, 'propertyManagement').mockResolvedValue({ canCreate: false, properties: [overview({ rooms: 2 })] })
  vi.spyOn(api, 'deletionImpact').mockResolvedValue({ id: 1, name: 'Countryside House', rooms: 2, devices: 0, installations: 0,
    historicalDevices: 1, members: 1, vaultProfiles: 1, dashboardGroups: 1, canDelete: true })
  const remove = vi.spyOn(api, 'deleteProperty').mockResolvedValue({ id: 1 })
  show(<PropertyManagement current={property} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Actions for Countryside House' }))
  fireEvent.click(screen.getByRole('menuitem', { name: 'Delete property…' }))
  const dialog = within(await screen.findByRole('dialog'))
  expect(await dialog.findByText('1 encrypted provider profile')).toBeInTheDocument()
  expect(dialog.getByText(/encrypted provider settings are kept/)).toBeInTheDocument()
  const confirm = dialog.getByRole('button', { name: 'Delete property' })
  fireEvent.change(dialog.getByRole('textbox'), { target: { value: 'countryside house' } })
  expect(confirm).toBeDisabled()
  fireEvent.change(dialog.getByRole('textbox'), { target: { value: 'Countryside House' } })
  list.mockResolvedValue({ canCreate: false, properties: [] })
  fireEvent.click(confirm)
  await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent(/^\/$/))
  expect(remove).toHaveBeenCalledWith(1, 'Countryside House')
  expect(screen.getByRole('status')).toHaveTextContent('Countryside House was deleted.')
})

it('shows the empty room state and explains that deleting a room keeps its devices', async () => {
  vi.spyOn(api, 'propertyManagement').mockResolvedValue({ canCreate: false, properties: [overview({})] })
  const rooms = vi.spyOn(api, 'managedRooms').mockResolvedValue([])
  show(<RoomManagement property={property} />)
  expect(await screen.findByText('No rooms yet. Add your first room.')).toBeInTheDocument()
  const create = vi.spyOn(api, 'createRoom').mockResolvedValue({ id: 4 })
  fireEvent.click(await screen.findByRole('button', { name: 'Add room' }))
  fireEvent.change(screen.getByLabelText('Room name'), { target: { value: 'Kitchen' } })
  rooms.mockResolvedValue([{ id: 4, name: 'Kitchen', description: null, devices: 2 }])
  fireEvent.click(within(screen.getByRole('form', { name: 'Add room' })).getByRole('button', { name: 'Add room' }))
  expect(await screen.findByText('Kitchen was added.')).toBeInTheDocument()
  expect(create).toHaveBeenCalledWith(1, { name: 'Kitchen', description: null })
  const remove = vi.spyOn(api, 'deleteRoom').mockResolvedValue({ id: 4 })
  fireEvent.click(await screen.findByRole('button', { name: 'Actions for Kitchen' }))
  fireEvent.click(screen.getByRole('menuitem', { name: 'Delete room…' }))
  const dialog = within(screen.getByRole('dialog', { name: 'Delete Kitchen?' }))
  expect(dialog.getByText(/Devices are not deleted/)).toBeInTheDocument()
  rooms.mockResolvedValue([])
  fireEvent.click(dialog.getByRole('button', { name: 'Delete room' }))
  expect(await screen.findByText('Kitchen was deleted. Its devices stay in Countryside House.')).toBeInTheDocument()
  expect(remove).toHaveBeenCalledWith(1, 4)
})

const device: Device = {
  id: 7, roomId: null, name: 'Boiler relay', kind: 'smartSwitch', manufacturer: 'SONOFF', model: 'PSF-X67', connectivity: 'online',
  faultCode: null, lastContactAt: null, quality: 'fresh', endpoints: [], providerName: 'PSF-X67 relay', provider: 'ewelink', customName: true,
} as Device

it('renames a device or clears its custom name from the device menu', async () => {
  const rename = vi.spyOn(api, 'renameDevice').mockResolvedValue({ id: 7 })
  show(<DeviceActions device={device} property={property} destinations={[]} />)
  fireEvent.click(screen.getByRole('button', { name: 'Actions for Boiler relay' }))
  expect(screen.queryByRole('menuitem', { name: /Move/ })).not.toBeInTheDocument()
  fireEvent.click(screen.getByRole('menuitem', { name: 'Rename…' }))
  const dialog = within(screen.getByRole('dialog'))
  expect(dialog.getByText(/The provider calls this device “PSF-X67 relay”/)).toBeInTheDocument()
  fireEvent.change(dialog.getByRole('textbox'), { target: { value: '' } })
  fireEvent.click(dialog.getByRole('button', { name: 'Use provider name' }))
  await waitFor(() => expect(rename).toHaveBeenCalledWith(1, 7, null))
})

it('moves a device to another property only after the server impact review', async () => {
  const impact = vi.spyOn(api, 'transferImpact').mockResolvedValue({ deviceId: 7, name: 'Boiler relay', provider: 'ewelink',
    sourcePropertyId: 1, sourcePropertyName: 'Countryside House', destinationPropertyId: 2, destinationPropertyName: 'Lake House',
    currentRoom: 'Utility', openLogicalAssignments: 1, blocked: null })
  const transfer = vi.spyOn(api, 'transferDevice').mockResolvedValue({ id: 70 })
  show(<DeviceActions device={device} property={property} destinations={[overview({ id: 2, name: 'Lake House' })]} />)
  fireEvent.click(screen.getByRole('button', { name: 'Actions for Boiler relay' }))
  fireEvent.click(screen.getByRole('menuitem', { name: 'Move to another property…' }))
  const dialog = within(screen.getByRole('dialog', { name: 'Move Boiler relay?' }))
  expect(await dialog.findByText(/History recorded so far stays with Countryside House/)).toBeInTheDocument()
  expect(dialog.getByText(/leaves the room Utility/)).toBeInTheDocument()
  expect(impact).toHaveBeenCalledWith(1, 7, 2, expect.anything())
  fireEvent.click(dialog.getByRole('button', { name: 'Move to Lake House' }))
  await waitFor(() => expect(transfer).toHaveBeenCalledWith(1, 7, 2))
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
})

it('offers Move to this property for devices in other properties and respects a blocked impact', async () => {
  vi.spyOn(api, 'transferableDevices').mockResolvedValue([{ deviceId: 12, propertyId: 2, propertyName: 'Lake House', name: 'Hall sensor',
    providerName: 'TH sensor', provider: 'tuya', model: 'TH01', roomName: null }])
  vi.spyOn(api, 'transferImpact').mockResolvedValue({ deviceId: 12, name: 'Hall sensor', provider: 'tuya', sourcePropertyId: 2,
    sourcePropertyName: 'Lake House', destinationPropertyId: 1, destinationPropertyName: 'Countryside House', currentRoom: null,
    openLogicalAssignments: 0, blocked: 'A command for this device is still awaiting readback.' })
  const transfer = vi.spyOn(api, 'transferDevice')
  show(<OtherPropertyDevices property={property} canManage />)
  expect(await screen.findByRole('heading', { name: 'Devices in your other properties' })).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Assign here' })).not.toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Actions for Hall sensor in Lake House' }))
  fireEvent.click(screen.getByRole('menuitem', { name: 'Move to this property…' }))
  const dialog = within(screen.getByRole('dialog'))
  expect(await dialog.findByText('A command for this device is still awaiting readback.')).toBeInTheDocument()
  expect(dialog.getByRole('button', { name: 'Move to Countryside House' })).toBeDisabled()
  expect(transfer).not.toHaveBeenCalled()
})

const daikinDevice = (patch: Partial<DaikinDevice>): DaikinDevice => ({
  id: 'unit', name: 'Upstairs', model: 'Fixture', roomId: null, connectivity: 'online', quality: 'fresh',
  observedAt: '2026-11-21T12:00:00Z', sourceAt: null, power: 'on', mode: 'heating', indoor: 18, target: 20, outdoor: null, humidity: null,
  fan: null, fanMode: null, fault: null, command: null, equipment: 'airConditioner', water: null,
  controls: { power: [], modes: [], temperature: null, fan: null }, ...patch,
})

function daikin(state: Partial<DaikinState>) {
  vi.spyOn(api, 'devicePreferences').mockResolvedValue({ order: {}, hidden: [] })
  vi.spyOn(api, 'rooms').mockResolvedValue([{ id: 3, name: 'Utility' }])
  vi.spyOn(api, 'daikin').mockResolvedValue({ status: 'connected', lastSuccess: null, manage: true, writes: false, devices: [],
    unassigned: [], ...state })
  show(<DaikinPanel property={property} />, '/properties/1/daikin')
}

it('keeps Refresh discovery visible and requires typing DISCONNECT before disconnecting ONECTA', async () => {
  daikin({})
  const update = vi.spyOn(api, 'updateDaikin').mockResolvedValue(undefined)
  const connect = vi.spyOn(api, 'connectDaikin')
  expect(await screen.findByRole('button', { name: 'Refresh discovery' })).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: /Disconnect account/ })).not.toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'ONECTA account actions' }))
  fireEvent.click(screen.getByRole('menuitem', { name: 'Disconnect account…' }))
  const dialog = within(screen.getByRole('dialog', { name: 'Disconnect the ONECTA account?' }))
  expect(dialog.getByText(/Reconnecting requires signing in to Daikin again/)).toBeInTheDocument()
  expect(dialog.getByText(/history and the encrypted provider settings in Settings are kept/)).toBeInTheDocument()
  const confirm = dialog.getByRole('button', { name: 'Disconnect account' })
  fireEvent.change(dialog.getByRole('textbox'), { target: { value: 'DISCONNECT ' } })
  expect(confirm).toBeDisabled()
  fireEvent.change(dialog.getByRole('textbox'), { target: { value: 'DISCONNECT' } })
  fireEvent.click(confirm)
  await waitFor(() => expect(update).toHaveBeenCalledWith(1, 'disconnect'))
  expect(update).toHaveBeenCalledTimes(1)
  expect(connect).not.toHaveBeenCalled()
})

it('asks for plain confirmation before starting the existing ONECTA reconnection flow', async () => {
  daikin({})
  const connect = vi.spyOn(api, 'connectDaikin').mockRejectedValue(new Error('offline'))
  fireEvent.click(await screen.findByRole('button', { name: 'ONECTA account actions' }))
  fireEvent.click(screen.getByRole('menuitem', { name: 'Reconnect account…' }))
  const dialog = within(screen.getByRole('dialog', { name: 'Reconnect the ONECTA account?' }))
  expect(dialog.queryByRole('textbox')).not.toBeInTheDocument()
  expect(connect).not.toHaveBeenCalled()
  fireEvent.click(dialog.getByRole('button', { name: 'Continue to Daikin' }))
  expect(await dialog.findByText(/reconnection could not start/)).toBeInTheDocument()
  expect(connect).toHaveBeenCalledWith(1)
})

it('keeps Assign here for unassigned Daikin devices and shows no inline room controls on device cards', async () => {
  daikin({ devices: [daikinDevice({ id: 'here', name: 'Living room AC', roomId: 3 })], unassigned: [daikinDevice({ id: 'new', name: 'New AC' })] })
  const assign = vi.spyOn(api, 'assignDaikin').mockResolvedValue(undefined)
  const button = await screen.findByRole('button', { name: 'Assign here' })
  expect(screen.getAllByRole('button', { name: 'Assign here' })).toHaveLength(1)
  expect(screen.getByRole('heading', { name: 'New AC' }).closest('.device-card')).toContainElement(button)
  expect(screen.getByRole('heading', { name: 'Living room AC' })).toBeInTheDocument()
  expect(screen.queryByRole('combobox')).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Save room' })).not.toBeInTheDocument()
  expect(screen.queryByText('No room')).not.toBeInTheDocument()
  expect(api.rooms).not.toHaveBeenCalled()
  const reads = vi.mocked(api.daikin).mock.calls.length
  fireEvent.click(button)
  await waitFor(() => expect(assign).toHaveBeenCalledExactlyOnceWith(1, 'new', null))
  await waitFor(() => expect(vi.mocked(api.daikin).mock.calls.length).toBeGreaterThan(reads))
})

it('keeps a failed Assign here on the card with an error', async () => {
  daikin({ unassigned: [daikinDevice({ id: 'new', name: 'New AC' })] })
  vi.spyOn(api, 'assignDaikin').mockRejectedValue(new Error('offline'))
  fireEvent.click(await screen.findByRole('button', { name: 'Assign here' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('Could not assign this device.')
  expect(screen.getByRole('button', { name: 'Assign here' })).toBeEnabled()
})

it('shows connect without account menu when ONECTA is disconnected', async () => {
  daikin({ status: 'disconnected' })
  expect(await screen.findByRole('button', { name: 'Connect account' })).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'ONECTA account actions' })).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Refresh discovery' })).not.toBeInTheDocument()
})
