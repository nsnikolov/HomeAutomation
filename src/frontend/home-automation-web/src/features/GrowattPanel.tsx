import { useQuery } from '@tanstack/react-query'
import { api } from '../shared/api'
import type { GrowattState, Property, Reading } from '../shared/types'
import { Card } from '../shared/ui'
import { formatMoment, humanize } from '../shared/utils'
import { DeviceBadge, formatReading, metricLabels } from './Devices'

const messages: Record<string, string> = {
  notConfigured: 'Live Growatt monitoring is not configured for this property.',
  invalidConfiguration: 'Check Growatt access, inverter mapping, time zone, and polling settings.',
  transportUnavailable: 'Waiting for authorized Growatt access and a verified read adapter.',
  waitingForSchema: 'Waiting for the database schema.',
  degraded: 'Growatt could not complete the latest poll. Readings may be stale.',
  paused: 'Growatt polling is backing off after a provider failure. Readings may be stale.',
  running: 'Live Growatt readings. Connection failures and stale readings are shown separately.',
}

const inverterMetricLabels = {
  activePower: 'Inverter output',
  gridFrequency: 'AC input frequency',
  gridVoltage: 'AC input voltage',
}

function deviceMetricLabel(metric: Reading['metric']) {
  return metric === 'activePower' || metric === 'gridFrequency' || metric === 'gridVoltage'
    ? inverterMetricLabels[metric] : metricLabels[metric]
}

export function GrowattPanel({ property }: { property: Property }) {
  const query = useQuery({
    queryKey: ['property', property.id, 'growatt'],
    queryFn: ({ signal }) => api.growatt(property.id, signal),
    refetchInterval: 30000,
  })
  return <GrowattSummary result={query.data} timeZone={property.timeZone}
    message={query.isError ? 'The Growatt status request failed.' : 'Checking Growatt integration…'} />
}

export function GrowattSummary({ result, timeZone, message }: { result?: GrowattState; timeZone: string; message?: string }) {
  const state = result?.data
  const master = state?.devices.find(device => device.name === 'Growatt master')
  const slave = state?.devices.find(device => device.name === 'Growatt slave')
  return <Card className="panel" aria-label="Growatt monitoring">
    <div className="card-heading"><div><h2>Growatt monitoring</h2><p>{result ? messages[result.status] ?? humanize(result.status) : message}</p></div></div>
    {result?.lastSuccess && <p>Last successful synchronization {formatMoment(result.lastSuccess, timeZone)}</p>}
    {state && <>
      <h3>Solar installation</h3>
      <dl className="device-readings">
        {(['pvPower', 'householdPower', 'gridAvailable', 'gridImportPower'] as const).map(metric =>
          <div key={metric}><dt>{metricLabels[metric]}</dt><dd><LiveReading reading={state.installation.find(item => item.metric === metric)} /></dd></div>)}
      </dl>
      {master?.connectivity === 'online' && slave?.connectivity === 'offline' &&
        <p className="notice">Growatt reports Master online and Slave offline. Check the Slave locally if this persists after recovery.</p>}
      <ul className="rows">{state.devices.map(device => {
        const observations = device.endpoints.flatMap(endpoint => endpoint.measurements)
        const latest = observations.map(reading => reading.observedAt).filter(at => at !== null)
          .sort((a, b) => Date.parse(b) - Date.parse(a))[0]
        return <li key={device.id}>
        <div>
          <h3>{device.name}</h3>
          {latest && <p>Latest source reading {formatMoment(latest, timeZone)} · {humanize(device.quality)}</p>}
          {device.connectivity === 'offline' && device.quality === 'fresh' &&
            <p>Growatt reports offline and has recently supplied readings.</p>}
          {device.faultCode && <p>Reported fault {device.faultCode}{device.connectivity === 'unknown' ? ' (last known)' : ''}</p>}
          {device.kind === 'battery' && !observations.some(reading => reading.metric === 'batteryPower' && reading.quality === 'fresh') &&
            <p>Shared battery flow unavailable</p>}
          <dl className="device-readings">{device.endpoints.flatMap(endpoint => endpoint.measurements.map(reading =>
            <div key={`${endpoint.id}:${reading.metric}`}><dt>{deviceMetricLabel(reading.metric)}</dt><dd><LiveReading reading={reading} /></dd></div>))}</dl>
        </div>
        {device.kind === 'inverter' && <DeviceBadge device={device} />}
      </li>})}</ul>
      {state.alerts.length > 0 && <>
        <h3>Recent Growatt alerts</h3>
        <ul className="rows">{state.alerts.map(alert => <li key={`${alert.deviceId}:${alert.code}:${alert.occurredAt}`}>
          {alert.deviceName} · {humanize(alert.code)} · {formatMoment(alert.occurredAt, timeZone)}
        </li>)}</ul>
      </>}
    </>}
  </Card>
}

function LiveReading({ reading }: { reading?: Reading }) {
  if (!reading || reading.quality === 'missing') return <>Unavailable</>
  if (reading.metric === 'gridAvailable' && reading.quality === 'stale') return <>Unknown (stale)</>
  if (reading.metric === 'warningStatus' && reading.code === '0')
    return <>{reading.quality === 'stale' ? 'None (stale)' : 'None'}</>
  const value = reading.metric === 'batteryPower' && reading.number !== null
    ? `${reading.number > 0 ? 'Charging' : reading.number < 0 ? 'Discharging' : 'Idle'} · ${Math.abs(reading.number).toLocaleString()} W`
    : formatReading(reading)
  return <>{value}{reading.quality === 'stale' ? ' (stale)' : ''}</>
}
