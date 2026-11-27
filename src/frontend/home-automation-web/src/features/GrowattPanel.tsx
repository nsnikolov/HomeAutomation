import { lazy, Suspense, useEffect } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router'
import { api } from '../shared/api'
import type { Device, GrowattState, InstallationValue, Property, PvInputState, Reading } from '../shared/types'
import { Badge, Card } from '../shared/ui'
import { displayNumber, formatMoment, humanize } from '../shared/utils'
import { DeviceBadge, deviceAvailability, formatReading, metricLabels } from './Devices'
import { DeviceCardOrder, useDevicePreferences } from './DeviceCardOrder'
// The power-flow view and its illustrations load on first use, like the other larger pages.
const GrowattFlow = lazy(() => import('./growatt/GrowattFlow').then(module => ({ default: module.GrowattFlow })))

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
  gridFrequency: 'AC input frequency',
  gridVoltage: 'AC input voltage',
}

const inverterPower = [
  { metric: 'activePower', label: 'Load/output power' },
  { metric: 'acInputPower', label: metricLabels.acInputPower },
  { metric: 'batteryPower', label: 'Battery charge/discharge' },
] as const

const dailyEnergyMetrics = ['pvEnergyToday', 'outputEnergyToday', 'batteryDischargedEnergyToday'] as const
const isDailyEnergy = (reading: Reading) => dailyEnergyMetrics.some(metric => metric === reading.metric)

function deviceMetricLabel(metric: Reading['metric']) {
  return metric === 'gridFrequency' || metric === 'gridVoltage'
    ? inverterMetricLabels[metric] : metricLabels[metric]
}

export type GrowattView = 'flow' | 'details'
export const growattViewKey = 'homeautomation:growatt-view'
const isView = (value: string | null): value is GrowattView => value === 'flow' || value === 'details'

function storedView(): GrowattView | null {
  try {
    const value = localStorage.getItem(growattViewKey)
    return isView(value) ? value : null
  } catch {
    return null
  }
}

/** “Поток | Детайли”: a real button group in the style of the Dashboard's Compact/Detailed toggle. */
export function GrowattViewToggle({ view, onChange }: { view: GrowattView; onChange: (view: GrowattView) => void }) {
  return <div className="monitor-view-toggle" role="group" aria-label="Изглед">
    {([['flow', 'Поток'], ['details', 'Детайли']] as const).map(([value, label]) =>
      <button key={value} type="button" aria-pressed={view === value} onClick={() => onChange(value)}>{label}</button>)}
  </div>
}

/**
 * The Live Growatt page. One query feeds both views, and live updates refresh it through the property query key. The choice is in
 * the URL (`?view=flow` or `?view=details`) and in localStorage; the default is the power-flow view. Details is the existing summary.
 */
export function GrowattPanel({ property }: { property: Property }) {
  const preferences = useDevicePreferences(property.id, 'growatt')
  const [params, setParams] = useSearchParams()
  const requested = params.get('view')
  const view: GrowattView = isView(requested) ? requested : storedView() ?? 'flow'
  useEffect(() => {
    try { localStorage.setItem(growattViewKey, view) } catch { /* the URL still carries the choice */ }
    if (requested !== view)
      setParams(current => { const next = new URLSearchParams(current); next.set('view', view); return next }, { replace: true })
  }, [view, requested, setParams])
  const query = useQuery({
    queryKey: ['property', property.id, 'growatt'],
    queryFn: ({ signal }) => api.growatt(property.id, signal),
    refetchInterval: 30000,
  })
  const message = query.isError ? 'The Growatt status request failed.' : 'Checking Growatt integration…'
  return <>
    <div className="growatt-view-bar"><GrowattViewToggle view={view} onChange={next =>
      setParams(current => { const updated = new URLSearchParams(current); updated.set('view', next); return updated })} /></div>
    {view === 'details'
      ? <GrowattSummary result={query.data} timeZone={property.timeZone} preferences={preferences} message={message} />
      : <Suspense fallback={<p role="status">Loading the power-flow view…</p>}>
        <GrowattFlow result={query.data} timeZone={property.timeZone} message={message} /></Suspense>}
  </>
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
      <InstallationSummary values={state.solar ?? []} />
      {reported(master) === 'online' && reported(slave) === 'offline' &&
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

const reported = (device?: Device) => device?.reportedConnectivity ?? device?.connectivity

const slotLabels: Record<InstallationValue['slot'], Record<InstallationValue['basis'], { label: string; scope: string }>> = {
  pvPower: {
    installation: { label: 'PV production', scope: 'Reported for the whole installation.' },
    inverters: { label: 'PV production', scope: 'Sum of the inverters’ PV input power. Each inverter measures its own PV input; 0–10 W shows as 0 W.' },
  },
  consumption: {
    installation: { label: 'Household consumption', scope: 'Reported for the whole installation.' },
    inverters: { label: 'Inverter output', scope: 'Sum of the inverters’ AC output to the loads they supply. Loads wired outside the inverters are not included.' },
  },
  gridSupply: {
    installation: { label: 'Grid availability', scope: 'Reported for the whole installation.' },
    inverters: { label: 'AC input supply', scope: 'From the inverters’ AC input voltage after the transfer switch: present above 180 V, absent when every inverter reads 10 V or less (the grid outage alert thresholds). It shows that AC supply is present, not which source feeds it.' },
  },
  acInput: {
    installation: { label: 'Grid import', scope: 'Reported for the whole installation.' },
    inverters: { label: 'AC input power', scope: 'Sum of the power the inverters draw through their AC inputs after the transfer switch. 0 W is a valid reading.' },
  },
}
const supplyText = { present: 'Present', absent: 'Absent', unknown: 'Unknown' } as const
const watts = new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 })

/** One installation value: a valid zero, a value, partial or stale data, or Unavailable with the reason. */
export function InstallationReading({ value }: { value?: InstallationValue }) {
  if (!value || value.state === 'unavailable') return <>Unavailable{value?.reason && <small className="muted summary-reason">{value.reason}</small>}</>
  const number = value.number === null ? null : `${watts.format(value.number)}${value.unit ? ` ${value.unit}` : ''}`
  const main = value.slot === 'gridSupply'
    ? `${supplyText[value.text ?? 'unknown']}${number && value.text !== 'unknown' ? ` · ${number}` : ''}` : number ?? 'Unknown'
  const flag = value.state === 'stale' ? ' (stale)' : value.state === 'partial' ? ' (partial)' : ''
  return <>{main}{flag}{value.reason && <small className="muted summary-reason">{value.reason}</small>}</>
}

function InstallationSummary({ values }: { values: InstallationValue[] }) {
  return <dl className="device-readings installation-summary">
    {(['pvPower', 'consumption', 'gridSupply', 'acInput'] as const).map(slot => {
      const value = values.find(item => item.slot === slot)
      const text = slotLabels[slot][value?.basis ?? 'inverters']
      return <div key={slot} data-state={value?.state ?? 'unavailable'}><dt title={text.scope}>{text.label}</dt>
        <dd><InstallationReading value={value} /></dd></div>
    })}
  </dl>
}

const isPv1Reading = (reading: Reading) => reading.metric === 'pv1Voltage'
const isPvReading = (reading: Reading) => isPv1Reading(reading) || reading.metric === 'pvVoltage' || reading.metric === 'pvPower'

function GrowattDeviceCard({ device, timeZone }: { device: Device; timeZone: string }) {
  const inverter = device.kind === 'inverter'
  const observations = device.endpoints.flatMap(endpoint => endpoint.measurements)
  const dailyEnergy = observations.filter(isDailyEnergy)
  const latest = observations.map(reading => reading.observedAt).filter(at => at !== null)
    .sort((a, b) => Date.parse(b) - Date.parse(a))[0]
  const pvInputs = device.endpoints.filter(endpoint => endpoint.measurements.some(isPvReading))
  const pv1Endpoint = pvInputs.find(endpoint => endpoint.measurements.some(isPv1Reading)) ?? pvInputs[0]
  const details = device.endpoints.flatMap(endpoint => endpoint.measurements
    .filter(reading => !inverter || (!isPvReading(reading) && !isDailyEnergy(reading) && !inverterPower.some(item => item.metric === reading.metric))).map(reading =>
      <div key={`${endpoint.id}:${reading.metric}`}><dt>{deviceMetricLabel(reading.metric)}</dt><dd><LiveReading reading={reading} /></dd></div>))

  return <Card className={inverter ? 'device-card growatt-device' : 'device-card growatt-device growatt-auxiliary'}
    aria-label={device.name}>
    <div className="device-card-top"><h3>{device.name}</h3>{inverter && <DeviceBadge device={device} />}</div>
    {latest && <p className="muted">Latest source reading {formatMoment(latest, timeZone)} · {humanize(device.quality)}</p>}
    {reported(device) === 'offline' && deviceAvailability(device) === 'online' &&
      <p className="muted">Online from recent readings. Growatt itself reports this inverter as offline.</p>}
    {device.faultCode && <p>Reported fault {device.faultCode}{deviceAvailability(device) !== 'online' ? ' (last known)' : ''}</p>}
    {device.kind === 'battery' && !observations.some(reading => reading.metric === 'batteryPower' && reading.quality === 'fresh') &&
      <p>Shared battery flow unavailable</p>}
    {inverter && (pvInputs.length ? pvInputs.map(endpoint =>
      <PvInput key={endpoint.id} readings={endpoint.measurements} generation={device.pvInputs?.find(item => item.endpointId === endpoint.id)}
        showPv1={endpoint === pv1Endpoint} name={pvInputs.length > 1 ? endpoint.name : undefined} />)
      : <PvInput readings={[]} showPv1 />)}
    {inverter && <div className="growatt-reading-panel">
      <div className="growatt-pv-heading"><h4>AC and battery power</h4></div>
      <dl className="growatt-pv-readings">
        {inverterPower.map(({ metric, label }) =>
          <div key={metric}><dt title={metric === 'acInputPower' ? 'Measured at this inverter’s AC input after the transfer switch' : undefined}>{label}</dt><dd><LiveReading reading={observations.find(reading => reading.metric === metric)} /></dd></div>)}
      </dl>
    </div>}
    {inverter && dailyEnergy.length > 0 && <div className="growatt-reading-panel">
      <div className="growatt-pv-heading"><h4>Energy today</h4></div>
      <dl className="growatt-pv-readings">
        {dailyEnergyMetrics.map(metric => {
          const reading = dailyEnergy.find(item => item.metric === metric)
          return reading && <div key={metric}><dt>{metricLabels[metric]}</dt><dd><LiveReading reading={reading} /></dd></div>
        })}
      </dl>
    </div>}
    {!!details.length && <dl className="device-readings">{details}</dl>}
  </Card>
}

/**
 * PV badge from the server's generation state: generating above 20 W, until 10 W or below, holding in between, never at 0 V.
 * Without that state (an older server) the same thresholds apply to the latest reading alone, without hysteresis.
 */
export function pvStatus(generation: PvInputState | undefined, power: Reading | undefined, voltage: Reading | undefined) {
  const fallback: PvInputState['state'] = !power || power.quality !== 'fresh' || power.number === null || power.number < 0 ? 'unknown'
    : voltage?.quality === 'fresh' && voltage.number === 0 ? 'notGenerating' : power.number > 20 ? 'generating' : 'notGenerating'
  const state = generation?.state ?? fallback
  const reason = generation?.reason
  const voltagePresent = voltage?.quality === 'fresh' && (voltage.number ?? 0) > 0
  if (state === 'generating') return { text: 'PV generating', tone: 'good' }
  if (state === 'notGenerating') return { text: reason === 'noVoltage' || voltage?.number === 0 ? 'No PV generation (0 V)'
    : voltagePresent ? 'PV voltage present, not generating' : 'No PV generation', tone: 'neutral' }
  if (reason === 'invalid' || (power?.number !== null && power?.number !== undefined && power.number < 0)) return { text: 'PV power invalid', tone: 'warn' }
  if (reason === 'stale' || power?.quality === 'stale') return { text: 'PV readings stale', tone: 'warn' }
  if (voltagePresent) return { text: 'PV voltage present, power unavailable', tone: 'neutral' }
  return { text: 'PV readings unavailable', tone: 'neutral' }
}

function PvInput({ readings, name, showPv1, generation }: { readings: Reading[]; name?: string; showPv1: boolean; generation?: PvInputState }) {
  const pv1 = readings.find(isPv1Reading)
  const generalVoltage = readings.find(reading => reading.metric === 'pvVoltage')
  const voltages = [pv1, generalVoltage]
  const voltage = voltages.find(reading => reading?.quality === 'fresh' && reading.number !== null) ??
    voltages.find(reading => reading?.quality === 'stale' && reading.number !== null)
  const power = readings.find(reading => reading.metric === 'pvPower')
  const status = pvStatus(generation, power, voltage)
  const raw = power?.number ?? null
  const adjusted = raw !== null && raw > 0 && displayNumber('pvPower', raw) === 0

  return <div className="growatt-reading-panel">
    <div className="growatt-pv-heading"><h4>{name ?? 'PV input'}</h4>
      <Badge className={`tone-${status.tone}`}>{status.text}</Badge>
    </div>
    <dl className="growatt-pv-readings">
      <div><dt>{showPv1 ? 'PV1 voltage' : 'PV voltage'}</dt><dd><LiveReading reading={voltage} /></dd></div>
      <div><dt>PV input power</dt><dd title={adjusted ? `Raw reading ${raw} W, shown as 0 W (0–10 W is treated as no production).` : undefined}>
        <LiveReading reading={power} /></dd></div>
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
