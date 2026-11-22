import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError } from '../shared/api'
import type { Property, Room, TuyaDevice, TuyaReading } from '../shared/types'
import { Badge, Button, Card } from '../shared/ui'
import { formatMoment, humanize } from '../shared/utils'
import { formatReading, metricLabels } from './Devices'

const statusText: Record<string, string> = {
  notConfigured: 'Tuya Cloud project credentials and a linked Smart Life account are required.',
  waitingForSchema: 'Apply the Tuya database migration to begin discovery.',
  waitingForSync: 'Waiting for the first Tuya synchronization.',
  syncing: 'Synchronizing with Tuya Cloud.',
  connected: 'Tuya Cloud synchronized.',
  partial: 'Some Tuya devices or specifications could not be read.',
  unavailableForProperty: 'This property has no assigned Tuya devices.',
  unavailable: 'Tuya Cloud is temporarily unavailable.',
  subscriptionExpired: 'The Tuya Cloud plan or API subscription has expired.',
  authenticationFailed: 'Tuya authentication failed. Check the project credentials and server clock.',
  accessDenied: 'Check the Tuya project region, API permissions, and linked account.',
  quotaExceeded: 'The Tuya Cloud usage quota is exhausted.',
  rateLimited: 'Tuya Cloud temporarily limited requests.',
  syncDeferred: 'The next discovery is scheduled later.',
}

export function TuyaPanel({ property }: { property: Property }) {
  const client = useQueryClient()
  const key = ['property', property.id, 'tuya']
  const query = useQuery({ queryKey: key, queryFn: ({ signal }) => api.tuya(property.id, signal), refetchInterval: 30_000 })
  const state = query.data
  const rooms = useQuery({
    queryKey: ['property', property.id, 'rooms'], queryFn: ({ signal }) => api.rooms(property.id, signal), enabled: !!state?.manage,
  })
  const refresh = useMutation({
    mutationFn: () => api.refreshTuya(property.id),
    onSettled: () => client.invalidateQueries({ queryKey: key }),
  })
  const deferred = state?.nextSync && Date.parse(state.nextSync) > Date.now()
  const errorCode = refresh.error instanceof ApiError ? refresh.error.code : undefined

  return <section aria-label="Smart Life / Tuya">
    <div className="page-heading"><h1>Smart Life / Tuya</h1>
      <p role="status">{query.isError ? 'Tuya status could not be loaded.'
        : state ? statusText[state.status] ?? 'Tuya could not synchronize.' : 'Loading Tuya Cloud…'}</p>
      {state?.lastSuccess && <p>Last synchronized {formatMoment(state.lastSuccess, property.timeZone)}</p>}
    </div>
    {state?.manage && <div className="filters">
      <Button variant="outline" onClick={() => refresh.mutate()} disabled={refresh.isPending || !!deferred}>Refresh discovery</Button>
      {deferred && state.nextSync && <p>Next sync {formatMoment(state.nextSync, property.timeZone)}</p>}
      {state.providerCode !== null && <p>Tuya error {state.providerCode}</p>}
      {refresh.isError && <p role="alert">{errorCode && statusText[errorCode] || 'Tuya discovery failed.'}</p>}
      {rooms.isError && <p role="alert">Rooms could not be loaded.</p>}
    </div>}
    {!!state?.unassigned.length && <section className="room"><h2>Unassigned devices</h2>
      <div className="device-grid">{state.unassigned.map(device =>
        <TuyaCard key={device.id} device={device} property={property} rooms={rooms.data} />)}</div>
    </section>}
    {!!state?.devices.length && <div className="device-grid">{state.devices.map(device =>
      <TuyaCard key={device.id} device={device} property={property} rooms={state.manage ? rooms.data : undefined} />)}</div>}
    {state?.manage && !state.devices.length && !state.unassigned.length && <p>No Tuya devices have been discovered.</p>}
  </section>
}

function TuyaCard({ device, property, rooms }: { device: TuyaDevice; property: Property; rooms?: Room[] }) {
  const client = useQueryClient()
  const [room, setRoom] = useState(device.roomId?.toString() ?? '')
  const assign = useMutation({
    mutationFn: () => api.assignTuya(property.id, device.id, room ? Number(room) : null),
    onSuccess: () => client.invalidateQueries({ queryKey: ['property', property.id, 'tuya'] }),
  })
  return <Card className="device-card">
    <div className="device-card-top"><h3>{device.name}</h3>
      <Badge className={device.quality === 'stale' ? 'tone-warn' : device.connectivity === 'online' ? 'tone-good' : 'tone-neutral'}>
        {humanize(device.quality === 'stale' ? 'stale' : device.connectivity)}
      </Badge>
    </div>
    {device.model && <p>{device.model}</p>}
    <p>Cloud snapshot received {formatMoment(device.receivedAt, property.timeZone)}</p>
    {!device.specificationsAvailable && <p>Device specification is unavailable.</p>}
    {device.specificationsAvailable && !device.readings.length && <p>No supported datapoints were reported in the specification.</p>}
    <dl className="device-readings">{device.readings.map(reading =>
      <div key={reading.code}><dt>{metricLabels[reading.metric]} · {reading.code}</dt><dd>{readingText(reading)}</dd></div>)}</dl>
    {rooms && <div className="filters">
      <select className="input select-input" aria-label={`Room for ${device.name}`} value={room}
        onChange={event => setRoom(event.target.value)} disabled={assign.isPending}>
        <option value="">No room</option>
        {rooms.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
      </select>
      <Button variant="outline" onClick={() => assign.mutate()} disabled={assign.isPending}>Assign here</Button>
      {assign.isError && <p role="alert">Could not assign this device.</p>}
    </div>}
  </Card>
}

function readingText(reading: TuyaReading) {
  if (reading.flag === null && reading.number === null) return 'Unavailable'
  const value = formatReading({ ...reading, observedAt: reading.reportedAt, code: null, quality: 'fresh' })
  if (reading.quality === 'stale') return `${value} · Last known`
  return reading.reportedAt ? value : `${value} · Report time unavailable`
}
