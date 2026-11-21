import { useQuery } from '@tanstack/react-query'
import { api } from '../shared/api'
import type { GrowattState, Property, Reading } from '../shared/types'
import { Card } from '../shared/ui'
import { formatMoment, humanize } from '../shared/utils'
import { DeviceBadge, formatReading, metricLabels } from './Devices'

const messages: Record<string, string> = {
  notConfigured: 'Live Growatt monitoring is not configured. The dashboard above shows simulated data.',
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
  return <Card className="panel" aria-label="Growatt monitoring">
    <div className="card-heading"><div><h2>Growatt monitoring</h2><p>{result ? messages[result.status] ?? humanize(result.status) : message}</p></div></div>
    {state && <>
      <h3>Solar installation</h3>
      <dl className="device-readings">
        {(['pvPower', 'householdPower', 'gridAvailable', 'gridImportPower'] as const).map(metric =>
          <div key={metric}><dt>{metricLabels[metric]}</dt><dd><LiveReading reading={state.installation.find(item => item.metric === metric)} /></dd></div>)}
      </dl>
      <ul className="rows">{state.devices.map(device => <li key={device.id}>
        <div>
          <h3>{device.name}</h3>
          {device.lastContactAt && <p>Last observation {formatMoment(device.lastContactAt, timeZone)}</p>}
          {device.faultCode && <p>Reported fault {device.faultCode}{device.connectivity === 'unknown' ? ' (last known)' : ''}</p>}
          <dl className="device-readings">{device.endpoints.flatMap(endpoint => endpoint.measurements.map(reading =>
            <div key={`${endpoint.id}:${reading.metric}`}><dt>{deviceMetricLabel(reading.metric)}</dt><dd><LiveReading reading={reading} /></dd></div>))}</dl>
        </div>
        {device.kind === 'inverter' && <DeviceBadge device={device} />}
      </li>)}</ul>
    </>}
  </Card>
}

function LiveReading({ reading }: { reading?: Reading }) {
  if (!reading || reading.quality === 'missing') return <>Unavailable</>
  const value = reading.metric === 'batteryPower' && reading.number !== null
    ? `${reading.number > 0 ? 'Charging' : reading.number < 0 ? 'Discharging' : 'Idle'} · ${Math.abs(reading.number).toLocaleString()} W`
    : formatReading(reading)
  return <>{value}{reading.quality === 'stale' ? ' (stale)' : ''}</>
}
