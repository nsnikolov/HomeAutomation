import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router'
import { api, LogicalApiError } from '../shared/api'
import type {
  LogicalAssignment, LogicalCreation, LogicalDevice, PhysicalDevice, Property, ReleasePreview, ReplacementCandidate, Room,
} from '../shared/types'
import { Badge, Button, Card, Input } from '../shared/ui'
import { formatMoment, humanize } from '../shared/utils'
import { metricLabels } from './Devices'

const providers: Record<string, string> = {
  growatt: 'Growatt', daikin: 'Daikin ONECTA', ewelink: 'eWeLink', tuya: 'Smart Life / Tuya', simulator: 'Simulator',
}
const providerName = (provider: string) => providers[provider] ?? humanize(provider)
const capabilityName = (capability: string) => humanize(capability).replace(/ Measurement$/, '')
const kinds = ['Sensor', 'SmartSwitch', 'HeatPump', 'AirConditioner', 'Inverter', 'Battery', 'Unknown']
const kindLabel = (kind: string) => kind === 'SmartSwitch' ? 'Relay / smart switch' : humanize(kind)
const creationLabels: Record<LogicalCreation, string> = { manual: 'Created manually', initialized: 'Initialized', automatic: 'Auto-created' }
const errorText = (error: unknown, fallback: string) => error instanceof LogicalApiError ? error.message : fallback

/** Value for a datetime-local input in this browser's time zone, to the minute. */
export function localInputValue(date: Date) {
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`
}

type Panel =
  | { type: 'create'; device?: PhysicalDevice }
  | { type: 'edit' | 'archive'; id: number }
  | { type: 'change'; id: number; channelKey: string; assignment: LogicalAssignment | null; initialDeviceId?: number }
  | { type: 'unassign'; id: number; assignment: LogicalAssignment }
  | { type: 'use'; device: PhysicalDevice }
type Filters = { room: string; provider: string; kind: string; status: 'all' | 'assigned' | 'unassigned'; lifecycle: 'active' | 'archived' }

export function LogicalDevices({ property }: { property: Property }) {
  const client = useQueryClient()
  const key = ['property', property.id, 'logical-devices']
  const list = useQuery({ queryKey: key, queryFn: ({ signal }) => api.logicalDevices(property.id, signal), retry: false })
  const physical = useQuery({
    queryKey: [...key, 'physical'], queryFn: ({ signal }) => api.physicalDevices(property.id, signal), retry: false, enabled: list.isSuccess,
  })
  const rooms = useQuery({ queryKey: ['property', property.id, 'rooms'], queryFn: ({ signal }) => api.rooms(property.id, signal), retry: false })
  const [panel, setPanel] = useState<Panel | null>(null)
  const [notice, setNotice] = useState('')
  const [tab, setTab] = useState<'logical' | 'hardware'>('logical')
  const [filters, setFilters] = useState<Filters>({ room: '', provider: '', kind: '', status: 'all', lifecycle: 'active' })
  const restore = useMutation({
    mutationFn: (id: number) => api.restoreLogicalDevice(property.id, id, null),
    onSuccess: async () => { setNotice('The logical device is active again. Assign a device to record new readings.'); await refresh() },
  })

  async function refresh() { await client.invalidateQueries({ queryKey: ['property', property.id] }) }
  function open(next: Panel | null) { setNotice(''); setPanel(next) }
  async function done(message: string) { setPanel(null); setNotice(message); await refresh() }

  if (list.isPending) return <p role="status">Loading logical devices…</p>
  if (list.isError) {
    const unavailable = list.error instanceof LogicalApiError && list.error.status === 503
    return <Card className="placeholder"><p className="muted">{unavailable
      ? 'Logical devices become available after the database upgrade (migrations 017, 018 and 019).'
      : 'Logical devices could not be loaded.'}</p></Card>
  }
  const { canManage, devices } = list.data
  const roomList: Room[] = rooms.data ?? []
  const roomName = (id: number | null) => roomList.find(room => room.id === id)?.name
  const hardware = physical.data?.devices ?? []
  const newHardware = hardware.filter(device => device.logicalDeviceId === null || device.logicalCreation === 'automatic')
  const shown = devices.filter(device => (filters.lifecycle === 'archived') === (device.archivedAt !== null) &&
    (!filters.room || String(device.roomId ?? '') === filters.room) && (!filters.kind || device.kind === filters.kind) &&
    (!filters.provider || device.current.some(item => item.provider === filters.provider)) &&
    (filters.status === 'all' || (filters.status === 'assigned') === (device.current.length > 0)))
  const providerOptions = [...new Set(devices.flatMap(device => [...device.current, ...device.previous].map(item => item.provider)))].sort()
  const setFilter = <K extends keyof Filters>(name: K, value: Filters[K]) => setFilters(current => ({ ...current, [name]: value }))

  return <section className="room" aria-labelledby="logical-devices-heading">
    <h2 id="logical-devices-heading">Logical devices</h2>
    <p className="muted">A logical device keeps one identity and one history while its physical hardware is replaced. New hardware gets an
      automatic identity once the property uses logical devices; you decide whether it is new equipment or a replacement.</p>
    {notice && <p role="status">{notice}</p>}
    <div className="monitor-actions" role="tablist" aria-label="Device management views">
      <Button type="button" role="tab" aria-selected={tab === 'logical'} variant={tab === 'logical' ? 'default' : 'outline'}
        onClick={() => setTab('logical')}>Logical devices</Button>
      <Button type="button" role="tab" aria-selected={tab === 'hardware'} variant={tab === 'hardware' ? 'default' : 'outline'}
        onClick={() => setTab('hardware')}>Unassigned and new hardware ({newHardware.length})</Button>
      {canManage && <Button type="button" variant="outline" onClick={() => open({ type: 'create' })}>Create logical device</Button>}
    </div>
    {panel?.type === 'create' && <LogicalDeviceForm property={property} rooms={roomList} hardware={hardware} initial={panel.device}
      onCancel={() => open(null)} onDone={done} />}

    {tab === 'hardware' && <HardwareView property={property} devices={newHardware} logical={devices} canManage={canManage}
      loading={physical.isPending} failed={physical.isError} roomName={roomName} panel={panel} open={open}
      onUse={(target, device) => {
        // Continue in the 7B replacement form of the chosen logical device, with this hardware preselected.
        const active = target.current.find(item => item.channelKey === 'main') ?? target.current[0] ?? null
        setFilters({ room: '', provider: '', kind: '', status: 'all', lifecycle: 'active' })
        setTab('logical')
        setNotice(`Choose the time ${device.name} took over ${target.name}, then confirm below.`)
        setPanel({ type: 'change', id: target.id, channelKey: active?.channelKey ?? 'main', assignment: active, initialDeviceId: device.id })
      }} />}

    {tab === 'logical' && <>
      {devices.length > 0 && <div className="monitor-actions" role="group" aria-label="Filter logical devices">
        <label>Room<select className="input select-input" value={filters.room} onChange={event => setFilter('room', event.target.value)}>
          <option value="">All rooms</option>
          {roomList.map(room => <option key={room.id} value={room.id}>{room.name}</option>)}
        </select></label>
        <label>Provider<select className="input select-input" value={filters.provider} onChange={event => setFilter('provider', event.target.value)}>
          <option value="">All providers</option>
          {providerOptions.map(item => <option key={item} value={item}>{providerName(item)}</option>)}
        </select></label>
        <label>Kind<select className="input select-input" value={filters.kind} onChange={event => setFilter('kind', event.target.value)}>
          <option value="">All kinds</option>
          {[...new Set(devices.map(device => device.kind))].sort().map(item => <option key={item} value={item}>{kindLabel(item)}</option>)}
        </select></label>
        <label>Assignment<select className="input select-input" value={filters.status}
          onChange={event => setFilter('status', event.target.value as Filters['status'])}>
          <option value="all">Any</option><option value="assigned">With a device</option><option value="unassigned">Without a device</option>
        </select></label>
        <label>Status<select className="input select-input" value={filters.lifecycle}
          onChange={event => setFilter('lifecycle', event.target.value as Filters['lifecycle'])}>
          <option value="active">Active</option><option value="archived">Archived</option>
        </select></label>
      </div>}
      {devices.length === 0 && <Card className="placeholder"><p className="muted">No logical devices yet. Create one, or ask an administrator to
        initialize the existing devices with the documented command. New hardware is then covered automatically.</p></Card>}
      {devices.length > 0 && shown.length === 0 && <p className="muted">No logical devices match these filters.</p>}
      {shown.map(device => <LogicalCard key={device.id} property={property} device={device} canManage={canManage} rooms={roomList}
        roomName={roomName(device.roomId)} panel={panel} open={open} onDone={done}
        onRestore={() => restore.mutate(device.id)} restoring={restore.isPending && restore.variables === device.id} />)}
      {restore.isError && <p role="alert">{errorText(restore.error, 'The logical device could not be restored.')}</p>}
    </>}
    {canManage && <DashboardBindings property={property} />}
  </section>
}

function LogicalCard({ property, device, canManage, rooms, roomName, panel, open, onDone, onRestore, restoring }: {
  property: Property; device: LogicalDevice; canManage: boolean; rooms: Room[]; roomName?: string; panel: Panel | null
  open: (panel: Panel | null) => void; onDone: (message: string) => void; onRestore: () => void; restoring: boolean
}) {
  const archived = device.archivedAt !== null
  const manage = canManage && !archived
  const channels = [...new Set([...device.current, ...device.previous].map(item => item.channelKey))]
  const emptyChannels = (channels.length ? channels : ['main']).filter(channel => !device.current.some(item => item.channelKey === channel))
  const mine = (type: Panel['type']) => panel?.type === type && 'id' in panel && panel.id === device.id
  return <Card className="detail-card">
    <div className="card-heading">
      <div><h3>{device.name}</h3>
        <p>{kindLabel(device.kind)}{roomName ? ` · ${roomName}` : ''}{archived ? ' · Archived' : ''}</p>
        {device.creation && device.creation !== 'manual' && <Badge className="tone-neutral">{creationLabels[device.creation]}</Badge>}
      </div>
      {(device.metrics ?? []).some(metric => metric.aggregation !== 'status') &&
        <Link className="text-link" to={`/properties/${property.id}/history?device=${device.id}`}>View history</Link>}
    </div>
    {device.description && <p className="muted">{device.description}</p>}
    {(device.metrics ?? []).length > 0 && <p className="muted">Measurements: {[...new Set(device.metrics!.map(item =>
      metricLabels[item.metric] ?? humanize(item.metric)))].join(', ')}</p>}
    {device.current.length === 0 && <p className="muted">{archived ? 'Archived logical devices keep their history but record no new readings.'
      : 'No physical device is currently assigned, so no current readings are shown.'}</p>}
    {device.current.map(assignment => <div key={assignment.id}>
      <CurrentDevice assignment={assignment} timeZone={property.timeZone} />
      {manage && !(mine('change') || mine('unassign')) && <div className="monitor-actions">
        <Button variant="outline" type="button" onClick={() => open({ type: 'change', id: device.id, channelKey: assignment.channelKey, assignment })}>
          Replace device{device.current.length > 1 ? ` (${assignment.channelKey})` : ''}
        </Button>
        <Button variant="ghost" type="button" onClick={() => open({ type: 'unassign', id: device.id, assignment })}>Unassign</Button>
      </div>}
      {panel?.type === 'change' && panel.id === device.id && panel.assignment?.id === assignment.id &&
        <ChangeDevice property={property} device={device} channelKey={assignment.channelKey} assignment={assignment}
          initialDeviceId={panel.initialDeviceId} onCancel={() => open(null)} onDone={onDone} />}
      {panel?.type === 'unassign' && panel.id === device.id && panel.assignment.id === assignment.id &&
        <UnassignDevice property={property} device={device} assignment={assignment} onCancel={() => open(null)} onDone={onDone} />}
    </div>)}
    {manage && emptyChannels.map(channel => panel?.type === 'change' && panel.id === device.id && panel.assignment === null && panel.channelKey === channel
      ? <ChangeDevice key={channel} property={property} device={device} channelKey={channel} assignment={null}
        initialDeviceId={panel.initialDeviceId} onCancel={() => open(null)} onDone={onDone} />
      : <Button key={channel} variant="outline" type="button"
        onClick={() => open({ type: 'change', id: device.id, channelKey: channel, assignment: null })}>
        Assign device{emptyChannels.length > 1 || channel !== 'main' ? ` (${channel})` : ''}</Button>)}
    {device.previous.length > 0 && <details>
      <summary>Previous devices ({device.previous.length})</summary>
      <ul>{device.previous.map(assignment => <li key={assignment.id}>
        {assignment.deviceName} · {providerName(assignment.provider)} · {period(assignment, property.timeZone)}
        {assignment.reason && <> · {assignment.reason}</>}
      </li>)}</ul>
    </details>}
    {manage && !mine('edit') && !mine('archive') && <div className="monitor-actions">
      <Button variant="ghost" type="button" onClick={() => open({ type: 'edit', id: device.id })}>Edit details</Button>
      <Button variant="ghost" type="button" onClick={() => open({ type: 'archive', id: device.id })}>Archive</Button>
    </div>}
    {mine('edit') && <LogicalDeviceForm property={property} rooms={rooms} device={device} onCancel={() => open(null)} onDone={onDone} />}
    {mine('archive') && <ArchiveDevice property={property} device={device} onCancel={() => open(null)} onDone={onDone} />}
    {canManage && archived && <Button variant="outline" type="button" disabled={restoring} onClick={onRestore}>Restore</Button>}
  </Card>
}

function period(assignment: LogicalAssignment, timeZone: string) {
  const start = assignment.validFrom.startsWith('1970-01-01') ? 'its first recorded reading' : formatMoment(assignment.validFrom, timeZone)
  return assignment.validTo ? `from ${start} until ${formatMoment(assignment.validTo, timeZone)}` : `since ${start}`
}

function CurrentDevice({ assignment, timeZone }: { assignment: LogicalAssignment; timeZone: string }) {
  return <div>
    <p><strong>{assignment.deviceName}</strong> · {providerName(assignment.provider)} · {assignment.model}
      {assignment.endpointName && <> · {assignment.endpointName}</>}</p>
    <p className="muted">Current device {period(assignment, timeZone)}</p>
    <ul className="capabilities" aria-label={`${assignment.deviceName} capabilities`}>
      {assignment.capabilities.map(capability => <li key={capability}><Badge className="tone-neutral">{capabilityName(capability)}</Badge></li>)}
    </ul>
  </div>
}

/** Create or edit a logical identity. Editing never changes its ID, assignments, history links or dashboard source keys. */
function LogicalDeviceForm({ property, rooms, device, hardware, initial, onCancel, onDone }: {
  property: Property; rooms: Room[]; device?: LogicalDevice; hardware?: PhysicalDevice[]; initial?: PhysicalDevice
  onCancel: () => void; onDone: (message: string) => void
}) {
  const [name, setName] = useState(device?.name ?? initial?.name ?? '')
  const [kind, setKind] = useState(device?.kind ?? initial?.kind ?? 'Sensor')
  const [room, setRoom] = useState(String(device?.roomId ?? initial?.roomId ?? ''))
  const [description, setDescription] = useState(device?.description ?? '')
  const [physical, setPhysical] = useState(String(initial?.id ?? ''))
  const [endpoint, setEndpoint] = useState('')
  const free = (hardware ?? []).filter(item => item.logicalDeviceId === null && item.capabilities.length > 0)
  const chosen = free.find(item => String(item.id) === physical)
  const input = { name: name.trim(), kind, roomId: room ? Number(room) : null, description: description.trim() || null }
  const save = useMutation({
    mutationFn: () => device
      ? api.updateLogicalDevice(property.id, device.id, { ...input, expectedUpdatedAt: device.updatedAt })
      : api.createLogicalDevice(property.id, { ...input, deviceId: chosen?.id ?? null, endpointId: endpoint ? Number(endpoint) : null }),
    onSuccess: () => onDone(device ? `${input.name} was saved.` : chosen ? `${input.name} was created and uses ${chosen.name}.`
      : `${input.name} was created without hardware. Assign a device to record readings.`),
  })
  const label = device ? `Edit ${device.name}` : 'Create a logical device'
  return <form className="detail-card" aria-label={label} onSubmit={event => {
    event.preventDefault()
    if (input.name && (!chosen?.requiresEndpoint || endpoint)) save.mutate()
  }}>
    <h4>{label}</h4>
    <label>Name<Input required maxLength={128} value={name} onChange={event => setName(event.target.value)} /></label>
    <label>Kind<select className="input select-input" value={kind} onChange={event => setKind(event.target.value)}>
      {[...new Set([...kinds, kind])].map(item => <option key={item} value={item}>{kindLabel(item)}</option>)}
    </select></label>
    <label>Room<select className="input select-input" value={room} onChange={event => setRoom(event.target.value)}>
      <option value="">No room</option>
      {rooms.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
    </select></label>
    <label>Description (optional)<Input maxLength={256} value={description} onChange={event => setDescription(event.target.value)} /></label>
    {!device && <>
      <label>Physical device (optional)<select className="input select-input" value={physical}
        onChange={event => { setPhysical(event.target.value); setEndpoint('') }}>
        <option value="">None yet — assign hardware later</option>
        {free.map(item => <option key={item.id} value={item.id}>{item.name} · {providerName(item.provider)} · {item.model}</option>)}
      </select></label>
      {chosen?.requiresEndpoint && <label>Endpoint<select className="input select-input" value={endpoint} onChange={event => setEndpoint(event.target.value)}>
        <option value="">Choose an endpoint</option>
        {chosen.endpoints.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
      </select></label>}
      {chosen && <p className="muted">{chosen.name} records into this logical device from now on. Its earlier readings are not moved.</p>}
    </>}
    {device && <p className="muted">Renaming or moving the device keeps its history, history links and dashboard measurements.</p>}
    {save.isError && <p role="alert">{errorText(save.error, 'The logical device could not be saved.')}</p>}
    <div className="monitor-actions">
      <Button type="submit" disabled={!input.name || save.isPending}>{save.isPending ? 'Saving…' : device ? 'Save changes' : 'Create'}</Button>
      <Button variant="ghost" type="button" onClick={onCancel}>Cancel</Button>
    </div>
  </form>
}

/**
 * The 7B replacement form, also used to assign hardware to a channel without a device. Hardware that another logical device uses
 * can be chosen explicitly; the preview states what stays with that device, and nothing is ended without confirmation.
 */
function ChangeDevice({ property, device, channelKey, assignment, initialDeviceId, onCancel, onDone }: {
  property: Property; device: LogicalDevice; channelKey: string; assignment: LogicalAssignment | null; initialDeviceId?: number
  onCancel: () => void; onDone: (message: string) => void
}) {
  const replacing = assignment !== null
  const candidates = useQuery({
    queryKey: ['property', property.id, 'logical-devices', device.id, 'candidates', channelKey],
    queryFn: ({ signal }) => api.replacementCandidates(property.id, device.id, channelKey, signal),
  })
  const [selected, setSelected] = useState(initialDeviceId ? String(initialDeviceId) : '')
  const [endpoint, setEndpoint] = useState('')
  const [effective, setEffective] = useState(() => localInputValue(new Date()))
  const [reason, setReason] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const [acceptMissing, setAcceptMissing] = useState(false)
  const [acceptHistory, setAcceptHistory] = useState(false)
  const [archiveReleased, setArchiveReleased] = useState<boolean | null>(null)
  const choices = useMemo(() => (candidates.data ?? []).filter(candidate => !candidate.current), [candidates.data])
  const candidate = choices.find(item => String(item.deviceId) === selected)
  const at = new Date(effective)
  const validTime = !Number.isNaN(at.getTime())
  const endpointId = endpoint ? Number(endpoint) : null
  const inUse = candidate?.assignedLogicalDeviceId != null && candidate.assignedLogicalDeviceId !== device.id
  const preview = useQuery({
    queryKey: ['property', property.id, 'logical-devices', device.id, 'release', candidate?.deviceId, endpointId, validTime ? at.toISOString() : ''],
    queryFn: ({ signal }) => api.releasePreview(property.id, device.id, candidate!.deviceId, endpointId, at.toISOString(), signal),
    enabled: inUse && validTime, retry: false,
  })
  const released: ReleasePreview | null = inUse ? preview.data ?? null : null
  const archiving = archiveReleased ?? (released?.leavesUnassigned === true && released.creation === 'automatic')
  const release = released ? { releaseFrom: released.logicalDeviceId, acceptReleasedHistory: acceptHistory, archiveReleased: archiving } : {}
  const change = useMutation({
    mutationFn: () => replacing
      ? api.replaceDevice(property.id, device.id, {
        channelKey, expectedAssignmentId: assignment.id, deviceId: candidate!.deviceId, endpointId,
        effectiveAt: at.toISOString(), reason: reason.trim() || null, acceptMissingCapabilities: acceptMissing, ...release,
      })
      : api.assignDevice(property.id, device.id, {
        channelKey, deviceId: candidate!.deviceId, endpointId, effectiveAt: at.toISOString(), reason: reason.trim() || null, ...release,
      }),
    onSuccess: () => onDone(replacing
      ? `${device.name} now uses ${candidate!.name}. Earlier readings stay with ${assignment.deviceName}.`
      : `${device.name} now uses ${candidate!.name}.`),
  })
  const unavailable = (item: ReplacementCandidate) => !item.compatible ? 'no matching measurement'
    : item.assignedLogicalDeviceName && item.assignedLogicalDeviceId !== device.id ? `in use by ${item.assignedLogicalDeviceName}; ends there` : null
  const releaseReady = !inUse || (released !== null && (released.readings === 0 || acceptHistory))
  const ready = candidate && candidate.compatible && validTime && confirmed && (!replacing || candidate.missing.length === 0 || acceptMissing) &&
    (!candidate.requiresEndpoint || endpoint !== '') && releaseReady
  const target = replacing ? assignment.deviceName : 'no device'

  return <form className="detail-card" aria-label={replacing ? `Replace the device of ${device.name}` : `Assign a device to ${device.name}`}
    onSubmit={event => {
      event.preventDefault()
      if (ready) change.mutate()
    }}>
    <h4>{replacing ? `Replace ${assignment.deviceName}` : `Assign a device${channelKey !== 'main' ? ` (${channelKey})` : ''}`}</h4>
    {candidates.isPending && <p role="status">Loading available devices…</p>}
    {candidates.isError && <p role="alert">Available devices could not be loaded.</p>}
    {candidates.data && <>
      <label>{replacing ? 'Replacement device' : 'Physical device'}<select className="input select-input" value={selected} onChange={event => {
        setSelected(event.target.value); setEndpoint(''); setConfirmed(false); setAcceptMissing(false); setAcceptHistory(false); setArchiveReleased(null)
      }}>
        <option value="">Choose a device already added to this property</option>
        {choices.map(item => <option key={item.deviceId} value={item.deviceId} disabled={!item.compatible}>
          {item.name} · {providerName(item.provider)} · {item.model}{unavailable(item) ? ` (${unavailable(item)})` : ''}
        </option>)}
      </select></label>
      {candidate?.requiresEndpoint && <label>Endpoint<select className="input select-input" value={endpoint}
        onChange={event => setEndpoint(event.target.value)}>
        <option value="">Choose the endpoint that takes over</option>
        {candidate.endpoints.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
      </select></label>}
      <label>{replacing ? 'Replacement time' : 'Start time'}<Input type="datetime-local" value={effective}
        onChange={event => { setEffective(event.target.value); setConfirmed(false); setAcceptHistory(false) }} /></label>
      <p className="muted">Times use this browser's time zone ({Intl.DateTimeFormat().resolvedOptions().timeZone}). The exact
        minute is kept; the hour is not rounded. The server rejects times in the future.</p>
      <label>Reason (optional)<Input maxLength={256} value={reason} onChange={event => setReason(event.target.value)} /></label>
    </>}
    {candidate && <div role="region" aria-label="Capability differences">
      {replacing && candidate.shared.length > 0 && <p>Continues: {candidate.shared.map(item => capabilityName(item)).join(', ')}.</p>}
      {replacing && candidate.added.length > 0 && <p>New measurements, recorded from the replacement time onwards: {candidate.added.map(item => capabilityName(item)).join(', ')}.</p>}
      {!replacing && <p>Measurements: {candidate.capabilities.map(item => capabilityName(item)).join(', ')}.</p>}
      {replacing && candidate.missing.length > 0 && <p role="alert">{candidate.name} does not report {candidate.missing.map(item => capabilityName(item)).join(', ')}.
        Earlier readings stay available, but no new readings will be recorded for {candidate.missing.length > 1 ? 'these measurements' : 'this measurement'}.</p>}
      {validTime && replacing && <p>Readings before {formatMoment(at.toISOString(), property.timeZone)} remain attributed to {assignment.deviceName}. From
        that time, {device.name} uses readings from {candidate.name}. No historical readings are deleted or changed, and nothing is sent
        to either device.</p>}
      {validTime && !replacing && <p>From {formatMoment(at.toISOString(), property.timeZone)}, {device.name} uses readings from {candidate.name}.
        No historical readings are changed, and nothing is sent to the device.</p>}
      {inUse && <ReleaseDetails preview={preview.isPending ? undefined : released} failed={preview.isError} device={candidate}
        acceptHistory={acceptHistory} onAcceptHistory={setAcceptHistory} archive={archiving} onArchive={setArchiveReleased}
        timeZone={property.timeZone} />}
      {replacing && candidate.missing.length > 0 && <label><input type="checkbox" checked={acceptMissing}
        onChange={event => setAcceptMissing(event.target.checked)} /> Continue without {candidate.missing.map(item => capabilityName(item)).join(', ')}</label>}
      <label><input type="checkbox" checked={confirmed} onChange={event => setConfirmed(event.target.checked)} /> I confirm
        that {candidate.name} {replacing ? `replaced ${target}` : `belongs to ${device.name}`} at this time</label>
    </div>}
    {change.isError && <p role="alert">{errorText(change.error, replacing ? 'The replacement could not be saved.' : 'The assignment could not be saved.')}</p>}
    <div className="monitor-actions">
      <Button type="submit" disabled={!ready || change.isPending}>{change.isPending ? 'Saving…' : replacing ? 'Save replacement' : 'Save assignment'}</Button>
      <Button variant="ghost" type="button" onClick={onCancel}>Cancel</Button>
    </div>
  </form>
}

function ReleaseDetails({ preview, failed, device, acceptHistory, onAcceptHistory, archive, onArchive, timeZone }: {
  preview: ReleasePreview | null | undefined; failed: boolean; device: ReplacementCandidate; acceptHistory: boolean
  onAcceptHistory: (value: boolean) => void; archive: boolean; onArchive: (value: boolean) => void; timeZone: string
}) {
  if (failed) return <p role="alert">The effect on {device.assignedLogicalDeviceName} could not be checked. Try again.</p>
  if (preview === undefined) return <p role="status">Checking {device.assignedLogicalDeviceName}…</p>
  if (preview === null) return <p role="alert">{device.name} is used by another logical device at a different time. Choose another time.</p>
  return <div role="region" aria-label={`Effect on ${preview.name}`}>
    <p>{device.name} is the current device of <strong>{preview.name}</strong>{preview.creation === 'automatic' ? ', which was created automatically when the hardware was discovered' : ''}.
      Saving ends that assignment at this time.</p>
    {preview.readings > 0
      ? <p role="alert">{preview.readings} reading{preview.readings === 1 ? '' : 's'} of {device.name}
        {preview.firstReading && preview.lastReading ? ` (${formatMoment(preview.firstReading, timeZone)} – ${formatMoment(preview.lastReading, timeZone)})` : ''}
        {' '}were recorded before this time. They stay with {preview.name} and are not moved. Choose an earlier time to keep them here instead.</p>
      : <p>No readings of {device.name} were recorded before this time, so nothing stays with {preview.name}.</p>}
    {preview.dashboardReferences > 0 && <p>{preview.dashboardReferences} dashboard measurement{preview.dashboardReferences === 1 ? ' uses' : 's use'} {preview.name}.
      They are not changed; edit the dashboard to point them at the logical device you keep.</p>}
    {preview.readings > 0 && <label><input type="checkbox" checked={acceptHistory} onChange={event => onAcceptHistory(event.target.checked)} /> Keep
      those readings with {preview.name}</label>}
    {preview.leavesUnassigned && <label><input type="checkbox" checked={archive} onChange={event => onArchive(event.target.checked)} /> Archive {preview.name} afterwards
      (its history stays available)</label>}
  </div>
}

function UnassignDevice({ property, device, assignment, onCancel, onDone }: {
  property: Property; device: LogicalDevice; assignment: LogicalAssignment; onCancel: () => void; onDone: (message: string) => void
}) {
  const [effective, setEffective] = useState(() => localInputValue(new Date()))
  const [reason, setReason] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const at = new Date(effective)
  const unassign = useMutation({
    mutationFn: () => api.unassignDevice(property.id, device.id, {
      channelKey: assignment.channelKey, expectedAssignmentId: assignment.id, effectiveAt: at.toISOString(), reason: reason.trim() || null,
    }),
    onSuccess: () => onDone(`${assignment.deviceName} no longer records into ${device.name}. Its readings stay in the history.`),
  })
  return <form className="detail-card" aria-label={`Unassign ${assignment.deviceName} from ${device.name}`} onSubmit={event => {
    event.preventDefault()
    if (confirmed && !Number.isNaN(at.getTime())) unassign.mutate()
  }}>
    <h4>Unassign {assignment.deviceName}</h4>
    <label>End time<Input type="datetime-local" value={effective} onChange={event => setEffective(event.target.value)} /></label>
    <label>Reason (optional)<Input maxLength={256} value={reason} onChange={event => setReason(event.target.value)} /></label>
    <p className="muted">Readings up to this time stay with {device.name}. The logical device then has no hardware until you assign one, and
      the hardware does not get a new identity automatically.</p>
    <label><input type="checkbox" checked={confirmed} onChange={event => setConfirmed(event.target.checked)} /> I confirm
      that {assignment.deviceName} stopped serving {device.name} at this time</label>
    {unassign.isError && <p role="alert">{errorText(unassign.error, 'The device could not be unassigned.')}</p>}
    <div className="monitor-actions">
      <Button type="submit" disabled={!confirmed || unassign.isPending}>{unassign.isPending ? 'Saving…' : 'Unassign'}</Button>
      <Button variant="ghost" type="button" onClick={onCancel}>Cancel</Button>
    </div>
  </form>
}

function ArchiveDevice({ property, device, onCancel, onDone }: {
  property: Property; device: LogicalDevice; onCancel: () => void; onDone: (message: string) => void
}) {
  const [reason, setReason] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const archive = useMutation({
    mutationFn: () => api.archiveLogicalDevice(property.id, device.id, reason.trim() || null),
    onSuccess: () => onDone(`${device.name} was archived. Its history stays available.`),
  })
  return <form className="detail-card" aria-label={`Archive ${device.name}`} onSubmit={event => {
    event.preventDefault()
    if (confirmed) archive.mutate()
  }}>
    <h4>Archive {device.name}</h4>
    <p className="muted">Archiving ends the current hardware assignment now and hides the device from the active list. History, audit and
      dashboard measurements are kept; dashboard measurements show it as archived. You can restore it later and assign hardware again.</p>
    <label>Reason (optional)<Input maxLength={256} value={reason} onChange={event => setReason(event.target.value)} /></label>
    <label><input type="checkbox" checked={confirmed} onChange={event => setConfirmed(event.target.checked)} /> Archive {device.name}</label>
    {archive.isError && <p role="alert">{errorText(archive.error, 'The logical device could not be archived.')}</p>}
    <div className="monitor-actions">
      <Button type="submit" disabled={!confirmed || archive.isPending}>{archive.isPending ? 'Saving…' : 'Archive'}</Button>
      <Button variant="ghost" type="button" onClick={onCancel}>Cancel</Button>
    </div>
  </form>
}

/** Hardware without an active logical device, and hardware whose identity was created automatically and may still need a decision. */
function HardwareView({ property, devices, logical, canManage, loading, failed, roomName, panel, open, onUse }: {
  property: Property; devices: PhysicalDevice[]; logical: LogicalDevice[]; canManage: boolean; loading: boolean; failed: boolean
  roomName: (id: number | null) => string | undefined; panel: Panel | null; open: (panel: Panel | null) => void
  onUse: (target: LogicalDevice, device: PhysicalDevice) => void
}) {
  if (loading) return <p role="status">Loading hardware…</p>
  if (failed) return <p role="alert">Hardware could not be loaded.</p>
  if (devices.length === 0) return <Card className="placeholder"><p className="muted">Every physical device is part of a logical device you manage.</p></Card>
  return <div>
    <p className="muted">Unassigned hardware is not necessarily offline or faulty. Auto-created devices are new equipment until you say otherwise.</p>
    {devices.map(device => <Card className="detail-card" key={device.id}>
      <div className="card-heading"><div>
        <h3>{device.name}</h3>
        <p>{providerName(device.provider)} · {device.model}{roomName(device.roomId) ? ` · ${roomName(device.roomId)}` : ''}</p>
      </div></div>
      <ul className="capabilities" aria-label={`${device.name} capabilities`}>
        {device.capabilities.map(capability => <li key={capability}><Badge className="tone-neutral">{capabilityName(capability)}</Badge></li>)}
      </ul>
      <p className="muted">{device.logicalDeviceId !== null
        ? `Recording into "${device.logicalDeviceName}", created automatically.`
        : device.everAssigned ? 'Not assigned. It previously served a logical device.' : 'Not assigned to a logical device.'}</p>
      {canManage && panel?.type === 'use' && panel.device.id === device.id
        ? <UseAsReplacement device={device} logical={logical} onCancel={() => open(null)} onChoose={target => onUse(target, device)} />
        : canManage && <div className="monitor-actions">
          {device.logicalDeviceId === null && device.capabilities.length > 0 &&
            <Button variant="outline" type="button" onClick={() => open({ type: 'create', device })}>Create logical device</Button>}
          {device.capabilities.length > 0 && <Button variant="outline" type="button" onClick={() => open({ type: 'use', device })}>
            {device.logicalDeviceId !== null ? 'Use as replacement for an existing logical device' : 'Assign to an existing logical device'}</Button>}
        </div>}
    </Card>)}
    <p className="muted">Readings at {property.name} are never moved between logical devices automatically.</p>
  </div>
}

function UseAsReplacement({ device, logical, onCancel, onChoose }: {
  device: PhysicalDevice; logical: LogicalDevice[]; onCancel: () => void; onChoose: (target: LogicalDevice) => void
}) {
  const targets = logical.filter(item => item.archivedAt === null && item.id !== device.logicalDeviceId)
  const [selected, setSelected] = useState('')
  const target = targets.find(item => String(item.id) === selected)
  return <form className="detail-card" aria-label={`Use ${device.name} for an existing logical device`} onSubmit={event => {
    event.preventDefault()
    if (target) onChoose(target)
  }}>
    <label>Logical device<select className="input select-input" value={selected} onChange={event => setSelected(event.target.value)}>
      <option value="">Choose the logical device this hardware serves</option>
      {targets.map(item => <option key={item.id} value={item.id}>{item.name}{item.current.length
        ? ` (currently ${item.current.map(current => current.deviceName).join(', ')})` : ' (no device)'}</option>)}
    </select></label>
    <p className="muted">The next step shows which measurements continue, the exact time, and what stays with the automatically created device.</p>
    <div className="monitor-actions">
      <Button type="submit" disabled={!target}>Continue</Button>
      <Button variant="ghost" type="button" onClick={onCancel}>Cancel</Button>
    </div>
  </form>
}

function DashboardBindings({ property }: { property: Property }) {
  const client = useQueryClient()
  const [open, setOpen] = useState(false)
  const [chosen, setChosen] = useState<string[]>([])
  const [message, setMessage] = useState('')
  const review = useQuery({
    queryKey: ['property', property.id, 'dashboard', 'logical-bindings'],
    queryFn: ({ signal }) => api.logicalBindings(property.id, signal), enabled: open, retry: false,
  })
  const convert = useMutation({
    mutationFn: () => api.convertLogicalBindings(property.id, review.data!.revision, chosen),
    onSuccess: async () => {
      setMessage(`${chosen.length} dashboard measurement${chosen.length === 1 ? '' : 's'} now follow logical devices.`)
      setChosen([])
      await client.invalidateQueries({ queryKey: ['property', property.id] })
    },
  })
  return <Card className="detail-card">
    <h3>Dashboard bindings</h3>
    <p className="muted">Dashboard measurements bound to a physical device stop updating when that device is replaced. Converting them to
      the logical device lets them follow the replacement. Nothing changes until you confirm.</p>
    {!open && <Button variant="outline" type="button" onClick={() => { setOpen(true); setMessage('') }}>Review dashboard bindings</Button>}
    {open && review.isPending && <p role="status">Checking dashboard measurements…</p>}
    {open && review.isError && <p role="alert">{review.error instanceof LogicalApiError && review.error.status === 403
      ? 'Only dashboard editors can convert bindings.' : 'Dashboard bindings could not be reviewed.'}</p>}
    {message && <p role="status">{message}</p>}
    {open && review.data && <>
      {review.data.proposals.length === 0 && <p>No physical measurements can be converted automatically.</p>}
      <ul>{review.data.proposals.map(proposal => <li key={proposal.valueId}><label>
        <input type="checkbox" checked={chosen.includes(proposal.valueId)} onChange={event => setChosen(event.target.checked
          ? [...chosen, proposal.valueId] : chosen.filter(id => id !== proposal.valueId))} />
        {' '}{proposal.widgetTitle}: {metricLabels[proposal.metric]} → {proposal.logicalName}
      </label></li>)}</ul>
      {review.data.skipped.length > 0 && <details><summary>Not converted ({review.data.skipped.length})</summary>
        <ul>{review.data.skipped.map(skip => <li key={skip.valueId}>{skip.sourceKey}: {skip.reason}</li>)}</ul></details>}
      {convert.isError && <p role="alert">{convert.error instanceof LogicalApiError ? convert.error.message : 'The bindings could not be converted.'}
        {' '}Review them again before retrying.</p>}
      <div className="monitor-actions">
        <Button type="button" disabled={chosen.length === 0 || convert.isPending} onClick={() => convert.mutate()}>Convert selected</Button>
        <Button variant="ghost" type="button" onClick={() => { setOpen(false); setChosen([]) }}>Close</Button>
      </div>
    </>}
  </Card>
}
