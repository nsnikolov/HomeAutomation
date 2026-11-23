import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useSearchParams } from 'react-router'
import { api, ApiError } from '../shared/api'
import type { EwelinkDevice, Property, Room } from '../shared/types'
import { Badge, Button, Card } from '../shared/ui'
import { formatMoment, humanize } from '../shared/utils'
import { DeviceCardOrder, useDevicePreferences } from './DeviceCardOrder'
import { formatReading } from './Devices'

const measurementRows = [
  ['temperature', 'Temperature'],
  ['humidity', 'Relative humidity'],
  ['activePower', 'Active power'],
  ['voltage', 'Voltage'],
  ['current', 'Current'],
  ['energy', 'Cumulative energy'],
] as const

const statuses: Record<string, string> = {
  notConfigured: 'eWeLink Cloud needs approved Developer credentials.',
  waitingForSchema: 'Apply the eWeLink database migration to connect.',
  disconnected: 'No eWeLink account is connected.',
  connected: 'eWeLink account connected.',
  syncing: 'Synchronizing with eWeLink.',
  partial: 'eWeLink returned a partial inventory.',
  unavailableForProperty: 'This property has no assigned eWeLink devices.',
  reauthorizationRequired: 'Reconnect to restore eWeLink access.',
  accessDenied: 'The Developer app may lack permission to read eWeLink devices.',
  accessOrQuotaLimited: 'eWeLink denied access or reached its request quota.',
  quotaExceeded: 'The eWeLink quota is exhausted.',
  rateLimited: 'eWeLink temporarily limited requests.',
  syncDeferred: 'The next discovery is scheduled later.',
  unavailable: 'eWeLink is temporarily unavailable.',
  httpsRequired: 'Open Home Automation from the HTTPS origin registered with eWeLink.',
}

export function EwelinkPanel({ property }: { property: Property }) {
  const preferences = useDevicePreferences(property.id, 'ewelink')
  const cache = useQueryClient()
  const [params] = useSearchParams()
  const key = ['property', property.id, 'ewelink']
  const query = useQuery({ queryKey: key, queryFn: ({ signal }) => api.ewelink(property.id, signal), refetchInterval: 30_000 })
  const view = query.data
  const rooms = useQuery({
    queryKey: ['property', property.id, 'rooms'], queryFn: ({ signal }) => api.rooms(property.id, signal), enabled: !!view?.manage,
  })
  const action = useMutation({
    mutationFn: async (operation: 'connect' | 'disconnect' | 'refresh') => {
      if (operation === 'connect') window.location.assign((await api.connectEwelink(property.id)).authorizationUrl)
      else await api.updateEwelink(property.id, operation)
    },
    onSettled: () => cache.invalidateQueries({ queryKey: key }),
  })
  const connected = view?.status !== 'disconnected'
  const deferred = view?.nextSync && Date.parse(view.nextSync) > Date.now()
  const errorCode = action.error instanceof ApiError ? action.error.code : undefined
  return <section aria-label="eWeLink Cloud">
    <div className="page-heading"><h1>eWeLink Cloud</h1>
      <p role="status">{query.isError ? 'eWeLink status could not be loaded.'
        : view ? statuses[view.status] ?? 'eWeLink could not synchronize.' : 'Loading eWeLink…'}</p>
      {view?.lastSuccess && <p>Last synchronized {formatMoment(view.lastSuccess, property.timeZone)}</p>}
    </div>
    {params.get('connect') === 'failed' && <p role="alert">eWeLink could not complete the connection or initial discovery.</p>}
    {params.get('connect') === 'success' && <p>eWeLink authorization completed.</p>}
    {view?.manage && <div className="filters">
      {(!connected || view.status === 'reauthorizationRequired') &&
        <Button onClick={() => action.mutate('connect')} disabled={action.isPending}>
          {connected ? 'Reconnect account' : 'Connect account'}
        </Button>}
      {connected && <>
        <Button variant="outline" onClick={() => action.mutate('refresh')}
          disabled={action.isPending || !!deferred || view.status === 'reauthorizationRequired'}>Refresh discovery</Button>
        <Button variant="outline" onClick={() => action.mutate('disconnect')} disabled={action.isPending}>Disconnect account</Button>
      </>}
      {deferred && view.nextSync && connected && <p>Next sync {formatMoment(view.nextSync, property.timeZone)}</p>}
      {action.isError && <p role="alert">{errorCode && statuses[errorCode] || 'eWeLink account action failed.'}</p>}
      {rooms.isError && <p role="alert">Rooms could not be loaded.</p>}
    </div>}
    {preferences.error && <p className="muted" role="alert">{preferences.error}</p>}
    {!!view?.devices.length && <section className="room"><h2>Assigned devices</h2>
      <DeviceCardOrder items={view.devices} group="assigned" preferences={preferences}>{device =>
        <EwelinkCard device={device} property={property} rooms={view.manage ? rooms.data : undefined} />}</DeviceCardOrder>
    </section>}
    {!!view?.unassigned.length && <section className="room"><h2>Unassigned devices</h2>
      <DeviceCardOrder items={view.unassigned} group="unassigned" preferences={preferences}>{device =>
        <EwelinkCard device={device} property={property} rooms={rooms.data} />}</DeviceCardOrder>
    </section>}
    {view?.manage && connected && !view.devices.length && !view.unassigned.length && <p>No eWeLink devices have been discovered.</p>}
  </section>
}

function EwelinkCard({ device, property, rooms }: { device: EwelinkDevice; property: Property; rooms?: Room[] }) {
  const client = useQueryClient()
  const [room, setRoom] = useState(device.roomId?.toString() ?? '')
  const assign = useMutation({
    mutationFn: () => api.assignEwelink(property.id, device.id, room ? Number(room) : null),
    onSuccess: () => client.invalidateQueries({ queryKey: ['property', property.id, 'ewelink'] }),
  })
  const relay = device.relay === null ? 'Unavailable' : device.relay ? 'On' : 'Off'
  const supported = device.supportedMetrics ?? device.measurements?.map(reading => reading.metric) ?? []
  const rows = measurementRows.filter(([metric]) => supported.includes(metric))
  return <Card className="device-card">
    <div className="device-card-top">
      <h3>{device.name}</h3>
      <Badge className={device.quality === 'stale' ? 'tone-warn' : device.connectivity === 'online' ? 'tone-good' : 'tone-neutral'}>
        {humanize(device.quality === 'stale' ? 'stale' : device.connectivity)}
      </Badge>
    </div>
    {device.model && <p>{device.model}</p>}
    <p>Cloud snapshot received {formatMoment(device.seenAt, property.timeZone)}</p>
    {(device.hasRelay || rows.length > 0) && <dl className="device-readings">
      {device.hasRelay && <div><dt>Relay</dt><dd>{relay}{device.relay !== null && device.relayQuality === 'stale' && ' · Last known'}</dd></div>}
      {rows.map(([metric, label]) => {
        const reading = device.measurements?.find(item => item.metric === metric)
        return <div key={metric}><dt>{label}</dt><dd>
          {reading && reading.quality !== 'missing' && reading.number !== null ? <>
            {formatReading(reading)}{reading.quality === 'stale' && ' · Last known'}
          </> : 'Unavailable'}
        </dd></div>
      })}
    </dl>}
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
