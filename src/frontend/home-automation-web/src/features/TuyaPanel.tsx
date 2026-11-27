import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError } from '../shared/api'
import type { Property, Room, TuyaDevice, TuyaReading } from '../shared/types'
import { Badge, Button, Card } from '../shared/ui'
import { formatMoment, humanize } from '../shared/utils'
import { formatReading, metricLabels } from './Devices'
import { DeviceCardOrder, orderDevices, useDevicePreferences } from './DeviceCardOrder'
import { SensorHistory } from './SensorHistory'

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
  const preferences = useDevicePreferences(property.id, 'tuya')
  const [showHidden, setShowHidden] = useState(false)
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
  const hidden = new Set(preferences.data.hidden)
  const isVisible = (device: TuyaDevice) => !hidden.has(device.id)
  const hiddenDevices = [
    ...orderDevices(state?.devices ?? [], preferences.data.order.assigned),
    ...orderDevices(state?.unassigned ?? [], preferences.data.order.unassigned),
  ].filter(device => !isVisible(device))
  const card = (device: TuyaDevice, assigned: boolean) =>
    <TuyaCard key={device.id} device={device} property={property} assigned={assigned}
      rooms={state?.manage ? rooms.data : undefined}
      visibility={preferences.ready ? { hidden: hidden.has(device.id), busy: preferences.busy,
        toggle: () => preferences.update({ id: device.id, hidden: !hidden.has(device.id) }) } : undefined} />

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
    {preferences.error && <p className="muted" role="alert">{preferences.error}</p>}
    {!!hiddenDevices.length && <Button variant="ghost" type="button" aria-expanded={showHidden}
      onClick={() => setShowHidden(value => !value)}>{showHidden ? 'Close hidden devices' : `Show hidden (${hiddenDevices.length})`}</Button>}
    {!preferences.loading && state?.devices.some(isVisible) && <section className="room"><h2>Assigned devices</h2>
      <DeviceCardOrder items={state.devices} group="assigned" preferences={preferences} visible={isVisible}>
        {device => card(device, true)}</DeviceCardOrder>
    </section>}
    {!preferences.loading && state?.unassigned.some(isVisible) && <section className="room"><h2>Unassigned devices</h2>
      <DeviceCardOrder items={state.unassigned} group="unassigned" preferences={preferences} visible={isVisible}>
        {device => card(device, false)}</DeviceCardOrder>
    </section>}
    {showHidden && !!hiddenDevices.length && <section className="room"><h2>Hidden devices</h2>
      <div className="device-grid">{hiddenDevices.map(device =>
        card(device, !!state?.devices.some(assigned => assigned.id === device.id)))}</div>
    </section>}
    {state?.manage && !state.devices.length && !state.unassigned.length && <p>No Tuya devices have been discovered.</p>}
  </section>
}

function TuyaCard({ device, property, rooms, assigned, visibility }: {
  device: TuyaDevice; property: Property; rooms?: Room[]; assigned: boolean;
  visibility?: { hidden: boolean; busy: boolean; toggle: () => void };
}) {
  const client = useQueryClient()
  const [room, setRoom] = useState(device.roomId?.toString() ?? '')
  const [history, setHistory] = useState(false)
  const assign = useMutation({
    mutationFn: () => api.assignTuya(property.id, device.id, room ? Number(room) : null),
    onSuccess: () => Promise.all([client.invalidateQueries({ queryKey: ['property', property.id] }),
      client.invalidateQueries({ queryKey: ['property-management'] })]),
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
    {(visibility || (assigned && device.readings.some(reading => reading.metric === 'temperature' || reading.metric === 'humidity'))) &&
      <div className="filters">
        {assigned && device.readings.some(reading => reading.metric === 'temperature' || reading.metric === 'humidity') &&
          <Button variant="ghost" type="button" aria-expanded={history} onClick={() => setHistory(value => !value)}>
            {history ? 'Close history' : 'Temperature and humidity history'}
          </Button>}
        {visibility && <Button variant="ghost" type="button" aria-label={`${visibility.hidden ? 'Restore' : 'Hide'} ${device.name}`}
          onClick={visibility.toggle} disabled={visibility.busy}>{visibility.hidden ? 'Restore' : 'Hide'}</Button>}
      </div>}
    {history && <SensorHistory property={property} device={device.id} load={api.tuyaHistory} source="tuya" />}
    {rooms && <div className="filters">
      <select className="input select-input" aria-label={`Room for ${device.name}`} value={room}
        onChange={event => setRoom(event.target.value)} disabled={assign.isPending}>
        <option value="">No room</option>
        {rooms.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
      </select>
      <Button variant="outline" onClick={() => assign.mutate()}
        disabled={assign.isPending || (assigned && room === (device.roomId?.toString() ?? ''))}>{assigned ? 'Save room' : 'Assign here'}</Button>
      {assign.isError && <p role="alert">{assigned ? 'Could not change the room.' : 'Could not assign this device.'}</p>}
    </div>}
  </Card>
}

function readingText(reading: TuyaReading) {
  if (reading.flag === null && reading.number === null) return 'Unavailable'
  const value = formatReading({ ...reading, observedAt: reading.reportedAt, code: null, quality: 'fresh' })
  if (reading.quality === 'stale') return `${value} · Last known`
  return reading.reportedAt ? value : `${value} · Report time unavailable`
}
