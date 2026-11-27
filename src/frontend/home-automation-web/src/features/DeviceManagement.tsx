import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, LogicalApiError } from '../shared/api'
import { ActionMenu } from '../shared/ActionMenu'
import { ConfirmDialog } from '../shared/ConfirmDialog'
import type { Device, DeviceAuditEntry, Property, PropertyOverview, TransferableDevice } from '../shared/types'
import { Card, Input } from '../shared/ui'
import { formatMoment } from '../shared/utils'
import { matchesSearch, searchTerms } from '../shared/search'

/** Providers whose devices can move between properties; Growatt follows its site configuration. */
export const transferableProviders = new Set(['daikin', 'ewelink', 'tuya'])
const failure = (error: unknown) => error instanceof LogicalApiError ? error.message : 'The action failed. Try again.'

/** The property-management view, shared by every management control so it loads once. */
export function useManagedProperties() {
  return useQuery({ queryKey: ['property-management'], queryFn: ({ signal }) => api.propertyManagement(signal), retry: false })
}

async function refreshDevices(client: ReturnType<typeof useQueryClient>, ...properties: number[]) {
  await Promise.all([
    client.invalidateQueries({ queryKey: ['property-management'] }),
    client.invalidateQueries({ queryKey: ['transferable-devices'] }),
    ...properties.map(id => client.invalidateQueries({ queryKey: ['property', id] })),
  ])
}

type Dialog = 'rename' | 'room' | 'move' | null

/** Three-dot actions for a device in this property: custom name, optional room and moving it to another property. */
export function DeviceActions({ device, property, destinations }: { device: Device; property: Property; destinations: PropertyOverview[] }) {
  const client = useQueryClient()
  const [dialog, setDialog] = useState<Dialog>(null)
  const movable = device.provider ? transferableProviders.has(device.provider) : false
  return <>
    <ActionMenu label={`Actions for ${device.name}`} items={[
      { label: 'Rename…', onSelect: () => setDialog('rename') },
      { label: 'Change room…', onSelect: () => setDialog('room') },
      ...(movable && destinations.length ? [{ label: 'Move to another property…', onSelect: () => setDialog('move') }] : []),
    ]} />
    {dialog === 'rename' && <RenameDialog device={device} property={property} onClose={() => setDialog(null)}
      onDone={() => refreshDevices(client, property.id)} />}
    {dialog === 'room' && <RoomDialog device={device} property={property} onClose={() => setDialog(null)}
      onDone={() => refreshDevices(client, property.id)} />}
    {dialog === 'move' && <MoveDialog device={{ id: device.id, name: device.name }} source={property.id} destinations={destinations}
      onClose={() => setDialog(null)} onDone={destination => refreshDevices(client, property.id, destination)} />}
  </>
}

function RenameDialog({ device, property, onClose, onDone }: { device: Device; property: Property; onClose: () => void; onDone: () => Promise<void> }) {
  const [name, setName] = useState(device.customName ? device.name : '')
  const providerName = device.providerName ?? device.name
  return <ConfirmDialog open title={`Rename ${device.name}`} confirmLabel={name.trim() ? 'Save name' : 'Use provider name'}
    errorText={failure} onCancel={onClose}
    onConfirm={async () => {
      await api.renameDevice(property.id, device.id, name.trim() || null)
      onClose()
      await onDone()
    }}>
    <label>Display name<Input value={name} maxLength={128} placeholder={providerName} onChange={event => setName(event.target.value)} /></label>
    <p className="muted">The provider calls this device “{providerName}”. A custom name survives synchronization and moves with the device.
      Leave it empty to use the provider name.</p>
  </ConfirmDialog>
}

function RoomDialog({ device, property, onClose, onDone }: { device: Device; property: Property; onClose: () => void; onDone: () => Promise<void> }) {
  const rooms = useQuery({ queryKey: ['property', property.id, 'rooms'], queryFn: ({ signal }) => api.rooms(property.id, signal) })
  const [room, setRoom] = useState(device.roomId?.toString() ?? '')
  return <ConfirmDialog open title={`Room for ${device.name}`} confirmLabel="Save room" errorText={failure} onCancel={onClose}
    blocked={room === (device.roomId?.toString() ?? '')}
    onConfirm={async () => {
      await api.setDeviceRoom(property.id, device.id, room ? Number(room) : null)
      onClose()
      await onDone()
    }}>
    <label>Room<select className="input select-input" value={room} onChange={event => setRoom(event.target.value)}>
      <option value="">No room</option>
      {rooms.data?.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
    </select></label>
    <p className="muted">A room is optional. Changing it does not affect history or provider settings.</p>
  </ConfirmDialog>
}

/** Moves a device from `source`; the impact is reviewed on the server for the chosen destination before confirmation. */
function MoveDialog({ device, source, destinations, onClose, onDone }: {
  device: { id: number; name: string }; source: number; destinations: Pick<PropertyOverview, 'id' | 'name'>[]
  onClose: () => void; onDone: (destination: number) => Promise<void>
}) {
  const [destination, setDestination] = useState(destinations.length === 1 ? destinations[0].id : 0)
  const impact = useQuery({
    queryKey: ['transfer-impact', source, device.id, destination],
    queryFn: ({ signal }) => api.transferImpact(source, device.id, destination, signal),
    enabled: destination > 0, staleTime: 0, gcTime: 0, retry: false,
  })
  const data = impact.data
  const target = destinations.find(item => item.id === destination)
  return <ConfirmDialog open title={`Move ${device.name}?`} confirmLabel={target ? `Move to ${target.name}` : 'Move device'} destructive
    blocked={!data || data.blocked !== null} errorText={failure} onCancel={onClose}
    onConfirm={async () => {
      await api.transferDevice(source, device.id, destination)
      onClose()
      await onDone(destination)
    }}>
    {destinations.length > 1 && <label>Destination property<select className="input select-input" value={destination}
      onChange={event => setDestination(Number(event.target.value))}>
      <option value={0}>Choose a property</option>
      {destinations.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
    </select></label>}
    {destination > 0 && impact.isPending && <p role="status">Checking the move…</p>}
    {impact.isError && <p role="alert">{failure(impact.error)}</p>}
    {data && <>
      <ul aria-label="Move impact">
        <li>From {data.sourcePropertyName} to {data.destinationPropertyName}.</li>
        <li>History recorded so far stays with {data.sourcePropertyName}; new readings belong to {data.destinationPropertyName}.</li>
        <li>{data.currentRoom ? `It leaves the room ${data.currentRoom} and arrives without a room.` : 'It arrives without a room.'}</li>
        {data.openLogicalAssignments > 0 && <li>{data.openLogicalAssignments} logical device assignment(s) in {data.sourcePropertyName} end
          at the moment of the move.</li>}
        <li>The custom name and provider connection move with the device. Manual relay controls must be enabled again.</li>
      </ul>
      {data.blocked && <p role="alert">{data.blocked}</p>}
    </>}
  </ConfirmDialog>
}

/** Devices in the user's other managed properties that can be moved here; unassigned provider devices stay on provider pages. */
export function OtherPropertyDevices({ property, canManage, search = '' }: { property: Property; canManage: boolean; search?: string }) {
  const client = useQueryClient()
  const devices = useQuery({
    queryKey: ['transferable-devices', property.id], queryFn: ({ signal }) => api.transferableDevices(property.id, signal),
    enabled: canManage, retry: false,
  })
  const [moving, setMoving] = useState<TransferableDevice | null>(null)
  const terms = searchTerms(search)
  const shown = (devices.data ?? []).filter(device => matchesSearch(terms, [device.name, device.providerName, device.propertyName, device.roomName,
    device.model]))
  // Hidden when nothing matches: the page shows one empty state for the whole search.
  if (!canManage || shown.length === 0) return null
  return <section className="room" aria-labelledby="other-devices-heading">
    <h2 id="other-devices-heading">Devices in your other properties</h2>
    <p className="muted">These devices belong to another property you manage. Moving one keeps its earlier history with that property.</p>
    <div className="management-list">
      {shown.map(device => <Card className="detail-card" key={`${device.propertyId}:${device.deviceId}`}>
        <div className="management-row">
          <div><h3>{device.name}</h3>
            <p className="muted">{device.propertyName}{device.roomName ? ` · ${device.roomName}` : ''} · {device.model}</p>
            {device.name !== device.providerName && <p className="muted">Provider name: {device.providerName}</p>}</div>
          <ActionMenu label={`Actions for ${device.name} in ${device.propertyName}`}
            items={[{ label: 'Move to this property…', onSelect: () => setMoving(device) }]} />
        </div>
      </Card>)}
    </div>
    {moving && <MoveDialog device={{ id: moving.deviceId, name: moving.name }} source={moving.propertyId}
      destinations={[{ id: property.id, name: property.name }]} onClose={() => setMoving(null)}
      onDone={() => refreshDevices(client, moving.propertyId, property.id)} />}
  </section>
}

const auditText: Record<string, (entry: DeviceAuditEntry) => string> = {
  deviceAssigned: entry => entry.detail ? `Assigned to this property: ${entry.detail}` : 'Assigned to this property',
  deviceRoomChanged: entry => entry.detail ? `Room changed to ${entry.detail}` : 'Room changed',
  deviceRenamed: entry => entry.detail ? `Renamed: ${entry.detail}` : 'Custom name cleared',
  deviceTransferredOut: entry => `Moved to ${entry.otherPropertyName ?? 'another property'}`,
  deviceTransferredIn: entry => `Moved here from ${entry.otherPropertyName ?? 'another property'}`,
  roomAssignmentsCleared: () => 'Room assignment cleared by an administrator',
}

/** Assignment, transfer, room and name changes recorded for a device, newest first. */
export function DeviceAudit({ property, device }: { property: Property; device: number }) {
  const audit = useQuery({
    queryKey: ['property', property.id, 'device', device, 'audit'], queryFn: ({ signal }) => api.deviceAudit(property.id, device, signal),
    retry: false,
  })
  return <Card className="detail-card" aria-labelledby="device-audit-heading">
    <h2 id="device-audit-heading">Device history</h2>
    {audit.isPending && <p role="status">Loading device history…</p>}
    {audit.isError && <p role="alert">Device history is unavailable.</p>}
    {audit.data?.length === 0 && <p className="muted">No assignment, room, name or property changes have been recorded.</p>}
    {!!audit.data?.length && <ol className="audit-list">{audit.data.map(entry => <li key={entry.id}>
      <time dateTime={entry.occurredAt}>{formatMoment(entry.occurredAt, property.timeZone)}</time>
      {' · '}{(auditText[entry.action] ?? (() => entry.action))(entry)}
    </li>)}</ol>}
  </Card>
}
