import { useQuery } from '@tanstack/react-query'
import { api } from '../shared/api'
import type { Device, GrowattState, Property, Reading } from '../shared/types'
import { Badge, Card } from '../shared/ui'
import { formatMoment, humanize } from '../shared/utils'
import { DeviceBadge, formatReading, metricLabels } from './Devices'
import { DeviceCardOrder, useDevicePreferences } from './DeviceCardOrder'

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
  const preferences = useDevicePreferences(property.id, 'growatt')
  const query = useQuery({
    queryKey: ['property', property.id, 'growatt'],
    queryFn: ({ signal }) => api.growatt(property.id, signal),
    refetchInterval: 30000,
  })
  return <GrowattSummary result={query.data} timeZone={property.timeZone} preferences={preferences}
    message={query.isError ? 'The Growatt status request failed.' : 'Checking Growatt integration…'} />
}

export function GrowattSummary({ result, timeZone, message, preferences }: {
  result?: GrowattState; timeZone: string; message?: string; preferences?: ReturnType<typeof useDevicePreferences>
}) {
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
      {preferences?.error && <p className="muted" role="alert">{preferences.error}</p>}
      <DeviceCardOrder className="growatt-inverters" items={state.devices} group="assigned" preferences={preferences}>
        {device => <GrowattDeviceCard device={device} timeZone={timeZone} />}
      </DeviceCardOrder>
      {state.alerts.length > 0 && <>
        <h3>Recent Growatt alerts</h3>
        <ul className="rows">{state.alerts.map(alert => <li key={`${alert.deviceId}:${alert.code}:${alert.occurredAt}`}>
          {alert.deviceName} · {humanize(alert.code)} · {formatMoment(alert.occurredAt, timeZone)}
        </li>)}</ul>
      </>}
    </>}
  </Card>
}

const isPvReading = (reading: Reading) => reading.metric === 'pvVoltage' || reading.metric === 'pvPower'

function GrowattDeviceCard({ device, timeZone }: { device: Device; timeZone: string }) {
  const inverter = device.kind === 'inverter'
  const observations = device.endpoints.flatMap(endpoint => endpoint.measurements)
  const latest = observations.map(reading => reading.observedAt).filter(at => at !== null)
    .sort((a, b) => Date.parse(b) - Date.parse(a))[0]
  const pvInputs = device.endpoints.filter(endpoint => endpoint.measurements.some(isPvReading))
  const details = device.endpoints.flatMap(endpoint => endpoint.measurements
    .filter(reading => !inverter || !isPvReading(reading)).map(reading =>
      <div key={`${endpoint.id}:${reading.metric}`}><dt>{deviceMetricLabel(reading.metric)}</dt><dd><LiveReading reading={reading} /></dd></div>))

  return <Card className={inverter ? 'device-card growatt-device' : 'device-card growatt-device growatt-auxiliary'}
    aria-label={device.name}>
    <div className="device-card-top"><h3>{device.name}</h3>{inverter && <DeviceBadge device={device} />}</div>
    {latest && <p className="muted">Latest source reading {formatMoment(latest, timeZone)} · {humanize(device.quality)}</p>}
    {device.connectivity === 'offline' && device.quality === 'fresh' &&
      <p>Growatt reports offline and has recently supplied readings.</p>}
    {device.faultCode && <p>Reported fault {device.faultCode}{device.connectivity === 'unknown' ? ' (last known)' : ''}</p>}
    {device.kind === 'battery' && !observations.some(reading => reading.metric === 'batteryPower' && reading.quality === 'fresh') &&
      <p>Shared battery flow unavailable</p>}
    {inverter && (pvInputs.length ? pvInputs.map(endpoint =>
      <PvInput key={endpoint.id} readings={endpoint.measurements} name={pvInputs.length > 1 ? endpoint.name : undefined} />)
      : <PvInput readings={[]} />)}
    {!!details.length && <dl className="device-readings">{details}</dl>}
  </Card>
}

function PvInput({ readings, name }: { readings: Reading[]; name?: string }) {
  const voltage = readings.find(reading => reading.metric === 'pvVoltage')
  const power = readings.find(reading => reading.metric === 'pvPower')
  const voltagePresent = voltage?.quality === 'fresh' && (voltage.number ?? 0) > 0
  const generating = power?.quality === 'fresh' && (power.number ?? 0) > 0
  const zeroPower = power?.quality === 'fresh' && power.number === 0
  const stale = voltage?.quality === 'stale' || power?.quality === 'stale'
  const status = generating ? 'PV generating' : voltagePresent ? 'PV voltage present' :
    zeroPower ? 'No PV generation reported' : stale ? 'PV readings stale' :
      voltage?.quality === 'fresh' ? 'PV power unavailable' : 'PV readings unavailable'

  return <div className="growatt-pv-input">
    <div className="growatt-pv-heading"><h4>{name ?? 'PV input'}</h4>
      <Badge className={generating ? 'tone-good' : stale && !voltagePresent && !zeroPower ? 'tone-warn' : 'tone-neutral'}>{status}</Badge>
    </div>
    <dl className="growatt-pv-readings">
      <div><dt>Voltage</dt><dd><LiveReading reading={voltage} /></dd></div>
      <div><dt>Current</dt><dd>Unavailable</dd></div>
      <div><dt>Power</dt><dd><LiveReading reading={power} /></dd></div>
    </dl>
  </div>
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
