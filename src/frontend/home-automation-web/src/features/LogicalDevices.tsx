import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router'
import { api, LogicalApiError } from '../shared/api'
import type { LogicalAssignment, LogicalDevice, Property, ReplacementCandidate } from '../shared/types'
import { Badge, Button, Card, Input } from '../shared/ui'
import { formatMoment, humanize } from '../shared/utils'
import { metricLabels } from './Devices'

const providers: Record<string, string> = {
  growatt: 'Growatt', daikin: 'Daikin ONECTA', ewelink: 'eWeLink', tuya: 'Smart Life / Tuya', simulator: 'Simulator',
}
const providerName = (provider: string) => providers[provider] ?? humanize(provider)
const capabilityName = (capability: string) => humanize(capability).replace(/ Measurement$/, '')

/** Value for a datetime-local input in this browser's time zone, to the minute. */
export function localInputValue(date: Date) {
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`
}

export function LogicalDevices({ property }: { property: Property }) {
  const key = ['property', property.id, 'logical-devices']
  const list = useQuery({ queryKey: key, queryFn: ({ signal }) => api.logicalDevices(property.id, signal), retry: false })
  const [replacing, setReplacing] = useState<{ device: LogicalDevice; assignment: LogicalAssignment } | null>(null)
  const [notice, setNotice] = useState('')

  if (list.isPending) return <p role="status">Loading logical devices…</p>
  if (list.isError) {
    const unavailable = list.error instanceof LogicalApiError && list.error.status === 503
    return <Card className="placeholder"><p className="muted">{unavailable
      ? 'Logical devices become available after the database upgrade (migrations 017 and 018).'
      : 'Logical devices could not be loaded.'}</p></Card>
  }
  const { canManage, devices } = list.data
  return <section className="room" aria-labelledby="logical-devices-heading">
    <h2 id="logical-devices-heading">Logical devices</h2>
    <p className="muted">A logical device keeps one identity and one history while its physical hardware is replaced.</p>
    {notice && <p role="status">{notice}</p>}
    {devices.length === 0 && <Card className="placeholder"><p className="muted">No logical devices yet. An administrator can create
      them for the existing devices with the documented initialization command.</p></Card>}
    {devices.map(device => <Card className="detail-card" key={device.id}>
      <div className="card-heading">
        <div><h3>{device.name}</h3><p>{humanize(device.kind)}{device.archivedAt ? ' · Archived' : ''}</p></div>
        {(device.metrics ?? []).some(metric => metric.aggregation !== 'status') &&
          <Link className="text-link" to={`/properties/${property.id}/history?device=${device.id}`}>View history</Link>}
      </div>
      {device.current.length === 0 && <p className="muted">No physical device is currently assigned.</p>}
      {device.current.map(assignment => <div key={assignment.id}>
        <CurrentDevice assignment={assignment} timeZone={property.timeZone} />
        {canManage && !device.archivedAt && replacing?.assignment.id !== assignment.id &&
          <Button variant="outline" type="button" onClick={() => { setNotice(''); setReplacing({ device, assignment }) }}>
            Replace device{device.current.length > 1 ? ` (${assignment.channelKey})` : ''}
          </Button>}
        {replacing?.assignment.id === assignment.id && <ReplaceDevice property={property} device={device} assignment={assignment}
          onCancel={() => setReplacing(null)} onDone={message => { setReplacing(null); setNotice(message) }} />}
      </div>)}
      {device.previous.length > 0 && <details>
        <summary>Previous devices ({device.previous.length})</summary>
        <ul>{device.previous.map(assignment => <li key={assignment.id}>
          {assignment.deviceName} · {providerName(assignment.provider)} · {period(assignment, property.timeZone)}
          {assignment.reason && <> · {assignment.reason}</>}
        </li>)}</ul>
      </details>}
    </Card>)}
    {canManage && <DashboardBindings property={property} />}
  </section>
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

function ReplaceDevice({ property, device, assignment, onCancel, onDone }: {
  property: Property; device: LogicalDevice; assignment: LogicalAssignment; onCancel: () => void; onDone: (message: string) => void
}) {
  const client = useQueryClient()
  const candidates = useQuery({
    queryKey: ['property', property.id, 'logical-devices', device.id, 'candidates', assignment.channelKey],
    queryFn: ({ signal }) => api.replacementCandidates(property.id, device.id, assignment.channelKey, signal),
  })
  const [selected, setSelected] = useState('')
  const [endpoint, setEndpoint] = useState('')
  const [effective, setEffective] = useState(() => localInputValue(new Date()))
  const [reason, setReason] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const [acceptMissing, setAcceptMissing] = useState(false)
  const choices = useMemo(() => (candidates.data ?? []).filter(candidate => !candidate.current), [candidates.data])
  const candidate = choices.find(item => String(item.deviceId) === selected)
  const at = new Date(effective)
  const validTime = !Number.isNaN(at.getTime())
  const replace = useMutation({
    mutationFn: () => api.replaceDevice(property.id, device.id, {
      channelKey: assignment.channelKey, expectedAssignmentId: assignment.id, deviceId: candidate!.deviceId,
      endpointId: endpoint ? Number(endpoint) : null, effectiveAt: at.toISOString(), reason: reason.trim() || null,
      acceptMissingCapabilities: acceptMissing,
    }),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ['property', property.id] })
      onDone(`${device.name} now uses ${candidate!.name}. Earlier readings stay with ${assignment.deviceName}.`)
    },
  })
  const unavailable = (item: ReplacementCandidate) => !item.compatible ? 'no matching measurement'
    : item.assignedLogicalDeviceName ? `in use by ${item.assignedLogicalDeviceName}` : null
  const ready = candidate && !unavailable(candidate) && validTime && confirmed && (candidate.missing.length === 0 || acceptMissing) &&
    (!candidate.requiresEndpoint || endpoint !== '')

  return <form className="detail-card" aria-label={`Replace the device of ${device.name}`} onSubmit={event => {
    event.preventDefault()
    if (ready) replace.mutate()
  }}>
    <h4>Replace {assignment.deviceName}</h4>
    {candidates.isPending && <p role="status">Loading available devices…</p>}
    {candidates.isError && <p role="alert">Available devices could not be loaded.</p>}
    {candidates.data && <>
      <label>Replacement device<select className="input select-input" value={selected} onChange={event => {
        setSelected(event.target.value); setEndpoint(''); setConfirmed(false); setAcceptMissing(false)
      }}>
        <option value="">Choose a device already added to this property</option>
        {choices.map(item => <option key={item.deviceId} value={item.deviceId} disabled={unavailable(item) !== null}>
          {item.name} · {providerName(item.provider)} · {item.model}{unavailable(item) ? ` (${unavailable(item)})` : ''}
        </option>)}
      </select></label>
      {candidate?.requiresEndpoint && <label>Endpoint<select className="input select-input" value={endpoint}
        onChange={event => setEndpoint(event.target.value)}>
        <option value="">Choose the endpoint that takes over</option>
        {candidate.endpoints.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
      </select></label>}
      <label>Replacement time<Input type="datetime-local" value={effective}
        onChange={event => { setEffective(event.target.value); setConfirmed(false) }} /></label>
      <p className="muted">Times use this browser's time zone ({Intl.DateTimeFormat().resolvedOptions().timeZone}). The exact
        minute is kept; the hour is not rounded. The server rejects times in the future.</p>
      <label>Reason (optional)<Input maxLength={256} value={reason} onChange={event => setReason(event.target.value)} /></label>
    </>}
    {candidate && <div role="region" aria-label="Capability differences">
      {candidate.shared.length > 0 && <p>Continues: {candidate.shared.map(item => capabilityName(item)).join(', ')}.</p>}
      {candidate.added.length > 0 && <p>New measurements, recorded from the replacement time onwards: {candidate.added.map(item => capabilityName(item)).join(', ')}.</p>}
      {candidate.missing.length > 0 && <p role="alert">{candidate.name} does not report {candidate.missing.map(item => capabilityName(item)).join(', ')}.
        Earlier readings stay available, but no new readings will be recorded for {candidate.missing.length > 1 ? 'these measurements' : 'this measurement'}.</p>}
      {validTime && <p>Readings before {formatMoment(at.toISOString(), property.timeZone)} remain attributed to {assignment.deviceName}. From
        that time, {device.name} uses readings from {candidate.name}. No historical readings are deleted or changed, and nothing is sent
        to either device.</p>}
      {candidate.missing.length > 0 && <label><input type="checkbox" checked={acceptMissing}
        onChange={event => setAcceptMissing(event.target.checked)} /> Continue without {candidate.missing.map(item => capabilityName(item)).join(', ')}</label>}
      <label><input type="checkbox" checked={confirmed} onChange={event => setConfirmed(event.target.checked)} /> I confirm
        that {candidate.name} replaced {assignment.deviceName} at this time</label>
    </div>}
    {replace.isError && <p role="alert">{replace.error instanceof LogicalApiError ? replace.error.message : 'The replacement could not be saved.'}</p>}
    <div className="monitor-actions">
      <Button type="submit" disabled={!ready || replace.isPending}>{replace.isPending ? 'Saving…' : 'Save replacement'}</Button>
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
