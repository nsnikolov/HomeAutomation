import { useDeferredValue, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ArrowLeft, ArrowUpRight, Battery, Cable, CircuitBoard, Search, Snowflake, Sun, Thermometer } from 'lucide-react'
import { Link, useParams } from 'react-router'
import { api } from '../shared/api'
import { matchesSearch, searchTerms } from '../shared/search'
import type { Availability, Device, Metric, Property, PropertyOverview, Reading } from '../shared/types'
import { Badge, Card, Input } from '../shared/ui'
import { displayNumber, formatMoment, humanize } from '../shared/utils'
import { DeviceCardOrder, useDevicePreferences } from './DeviceCardOrder'
import { logicalDeviceMatches, logicalDevicesKey, LogicalDevices } from './LogicalDevices'
import { DeviceActions, DeviceAudit, OtherPropertyDevices, useManagedProperties } from './DeviceManagement'

export const metricLabels: Record<Metric, string> = {
  pvPower: 'PV production', householdPower: 'Household consumption', gridAvailable: 'Grid availability',
  batterySoc: 'Battery charge', batteryPower: 'Battery power', voltage: 'Voltage', current: 'Current',
  activePower: 'Active power', energy: 'Energy counter', temperature: 'Temperature', humidity: 'Relative humidity',
  switchOn: 'Switch state', targetTemperature: 'Target temperature', operatingMode: 'Operating mode',
  gridVoltage: 'Grid voltage', gridFrequency: 'Grid frequency', gridImportPower: 'Grid import',
  pvVoltage: 'PV voltage', pv1Voltage: 'PV1 voltage', warningStatus: 'Reported warning',
  acOutputVoltage: 'AC output voltage', inverterLoadPercent: 'Inverter load', acChargingPower: 'AC charging power',
  acInputPower: 'Import from grid',
  pvEnergyToday: 'PV generation',
  outputEnergyToday: 'Load/output energy',
  batteryDischargedEnergyToday: 'Battery discharged',
  outdoorTemperature: 'Outdoor temperature', fanSpeed: 'Fan speed',
  leavingWaterTemperature: 'Leaving water', targetLeavingWaterTemperature: 'Water target',
  heatingControlMode: 'Heating control', waterSetpointMode: 'Water setpoint mode',
  heatingWarning: 'Heating warning', heatingEmergency: 'Heating emergency',
}

export function DeviceIcon({ kind, size = 18 }: { kind: string; size?: number }) {
  const Icon = ({ inverter: Sun, battery: Battery, airConditioner: Snowflake, heatPump: Thermometer, smartSwitch: Cable, sensor: Thermometer })[kind] ?? CircuitBoard
  return <Icon size={size} aria-hidden />
}

const availabilityLabels: Record<Availability, string> = { online: 'Online', stale: 'Stale', offline: 'Offline', unknown: 'Unknown' }

/** The application availability; older responses without it fall back to connectivity and reading freshness. */
export function deviceAvailability(device: Device): Availability {
  return device.availability ?? (device.connectivity === 'offline' ? 'offline' : device.quality === 'stale' ? 'stale' : device.connectivity)
}

function duration(seconds: number) {
  const minutes = Math.floor(seconds / 60)
  const rest = Math.round(seconds % 60)
  return minutes === 0 ? `${rest} s` : rest === 0 ? `${minutes} min` : `${minutes} min ${rest} s`
}

/** Why the badge says what it says, including the provider's own report when it differs. */
export function availabilityNote(device: Device) {
  const window = device.availabilityWindowSeconds ? duration(device.availabilityWindowSeconds) : null
  const basis = device.availabilityBasis
  const text = basis === 'recentReading' ? `A reading arrived within the last ${window ?? 'availability window'}.`
    : basis === 'providerOnline' ? `The provider reported it online within the last ${window ?? 'availability window'}.`
      : basis === 'providerOffline' ? 'The provider reported a disconnection, and no newer reading has arrived.'
        : basis === 'noRecentEvidence' ? `No reading or status within the last ${window ?? 'availability window'}. This is not a confirmed disconnection.`
          : basis === 'noEvidence' ? 'No reading or status has been received yet.' : null
  const reported = device.reportedConnectivity && device.reportedConnectivity !== 'unknown' &&
    device.reportedConnectivity !== deviceAvailability(device) ? ` The provider reports it as ${device.reportedConnectivity}.` : ''
  return text ? `${text}${reported}` : undefined
}

export function DeviceBadge({ device }: { device: Device }) {
  const status = device.faultCode ? 'Fault' : availabilityLabels[deviceAvailability(device)]
  const tone = ({ Fault: 'bad', Offline: 'neutral', Unknown: 'neutral', Stale: 'warn', Online: 'good' })[status]
  return <Badge className={`tone-${tone}`} title={availabilityNote(device)}><i />{status}</Badge>
}

export function formatReading(reading: Reading | undefined) {
  if (!reading || reading.quality === 'missing') return '—'
  if (reading.flag !== null) {
    if (reading.metric === 'gridAvailable') return reading.flag ? 'Available' : 'Outage'
    return reading.flag ? 'On' : 'Off'
  }
  if (reading.number !== null) {
    const shown = displayNumber(reading.metric, reading.number)
    if (shown === null) return 'Invalid'
    const value = new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 }).format(shown)
    return reading.unit === '%' ? `${value}%` : `${value}${reading.unit ? ' ' + reading.unit : ''}`
  }
  return reading.code ?? '—'
}

const providerLabels: Record<string, string> = {
  growatt: 'Growatt', daikin: 'Daikin ONECTA', ewelink: 'eWeLink', tuya: 'Smart Life Tuya', simulator: 'Simulator',
}

/** Search fields of a provider device: custom and provider names, model, manufacturer, provider, kind, room, channels and #id. */
export function deviceMatches(device: Device, terms: string[], roomName?: string) {
  return matchesSearch(terms, [device.name, device.providerName, device.manufacturer, device.model, device.provider,
    device.provider ? providerLabels[device.provider] : null, humanize(device.kind), roomName, `#${device.id}`,
    ...device.endpoints.map(endpoint => endpoint.name)])
}

export function Devices({ property, devices }: { property: Property; devices?: Device[] }) {
  const [search, setSearch] = useState('')
  // Filtering is local to the loaded data; deferring keeps typing responsive on properties with many devices.
  const query = useDeferredValue(search)
  const terms = searchTerms(query)
  const [kind, setKind] = useState('')
  const preferences = useDevicePreferences(property.id, 'devices')
  const rooms = useQuery({ queryKey: ['property', property.id, 'rooms'], queryFn: ({ signal }) => api.rooms(property.id, signal) })
  // The same query the logical device section runs, so this adds no request; it tells the page whether anything matched.
  const logical = useQuery({ queryKey: logicalDevicesKey(property.id), queryFn: ({ signal }) => api.logicalDevices(property.id, signal),
    retry: false })
  const roomName = (id: number | null) => rooms.data?.find(room => room.id === id)?.name
  const matches = (device: Device) => (!kind || device.kind === kind) && deviceMatches(device, terms, roomName(device.roomId))
  const groups = [...(rooms.data ?? []), { id: null, name: 'No room' }]
  const managed = useManagedProperties().data?.properties ?? []
  const canManage = managed.some(item => item.id === property.id && item.canManage)
  const destinations = managed.filter(item => item.id !== property.id && item.canManage)
  const logicalMatch = (logical.data?.devices ?? []).some(device => !device.archivedAt && logicalDeviceMatches(device, terms, roomName(device.roomId)))
  const nothing = devices && !devices.some(matches) && (terms.length === 0 || !logicalMatch)

  return <>
    <div className="page-heading"><h1>Devices</h1><p>Monitor device state, readings and available capabilities.</p></div>
    <div className="filters">
      <label className="search"><Search size={17} aria-hidden /><Input type="search" placeholder="Search devices" aria-label="Search devices"
        value={search} onChange={event => setSearch(event.target.value)} /></label>
      <select className="input select-input" aria-label="Device type" value={kind} onChange={event => setKind(event.target.value)}>
        <option value="">All device types</option>
        {[...new Set((devices ?? []).map(device => device.kind))].map(item => <option key={item} value={item}>{humanize(item)}</option>)}
      </select>
    </div>
    {(!devices || rooms.isPending) && <p role="status">Loading devices…</p>}
    {rooms.isError && <p role="alert">Rooms could not be loaded.</p>}
    {nothing && <Card className="placeholder" role="status">{terms.length ? `No matching devices for “${query.trim()}”.`
      : devices.length === 0 ? 'No devices in this property yet.' : 'No devices match your filters.'}</Card>}
    {preferences.error && <p className="muted" role="alert">{preferences.error}</p>}
    <LogicalDevices property={property} search={query} />
    <OtherPropertyDevices property={property} canManage={canManage} search={query} />
    {groups.map(room => {
      const items = (devices ?? []).filter(device => device.roomId === room.id)
      if (!items.some(matches)) return null
      return <section className="room" key={room.id ?? 'unassigned'}>
        <h2>{room.name}</h2>
        <DeviceCardOrder items={items} group={room.id === null ? 'unassigned' : `room-${room.id}`}
          preferences={preferences} visible={matches}>{device =>
          <DeviceCard device={device} property={property} destinations={canManage ? destinations : undefined} />}</DeviceCardOrder>
      </section>
    })}
  </>
}

function DeviceCard({ device, property, destinations }: { device: Device; property: Property; destinations?: PropertyOverview[] }) {
  const readings = device.endpoints.flatMap(endpoint => endpoint.measurements).slice(0, 3)
  return <Card className="device-card">
    <div className="device-card-top">
      <span className="device-icon"><DeviceIcon kind={device.kind} /></span>
      <span className="device-card-actions"><DeviceBadge device={device} />
        {destinations && <DeviceActions device={device} property={property} destinations={destinations} />}</span>
    </div>
    <h3><Link to={`/properties/${property.id}/devices/${device.id}`}>{device.name}<ArrowUpRight size={15} /></Link></h3>
    <p className="muted">{device.model}</p>
    <dl className="device-readings">{readings.map((reading, index) => <div key={`${reading.metric}:${index}`}>
      <dt>{metricLabels[reading.metric]}</dt><dd>{formatReading(reading)}</dd>
    </div>)}</dl>
  </Card>
}

export function DevicePage({ property }: { property: Property }) {
  const deviceId = Number(useParams().deviceId)
  const details = useQuery({
    queryKey: ['property', property.id, 'device', deviceId],
    queryFn: ({ signal }) => api.device(property.id, deviceId, signal),
    enabled: Number.isSafeInteger(deviceId) && deviceId > 0,
    refetchInterval: 15000,
  })
  const device = details.data
  return <>
    <Link className="text-link" to={`/properties/${property.id}/devices`}><ArrowLeft size={16} /> All devices</Link>
    {details.isError && <p role="alert">Device details are unavailable.</p>}
    {!device && details.isPending && <p role="status">{details.fetchStatus === 'idle' ? 'Device unavailable.' : 'Loading device…'}</p>}
    {device && <>
      <div className="page-heading"><h1>{device.name}</h1><p>{device.manufacturer} · {device.model}</p>
        {device.customName && device.providerName && <p>Provider name: {device.providerName}</p>}</div>
      <Card className="detail-state">
        <DeviceBadge device={device} />
        <p>Last contact {device.lastContactAt ? formatMoment(device.lastContactAt, property.timeZone) : '—'}</p>
        {device.faultCode && <p role="status">Reported fault {device.faultCode}</p>}
      </Card>
      {device.endpoints.map(endpoint => <Card className="detail-card" key={endpoint.id}>
        <h2>{endpoint.name} capabilities</h2>
        <ul className="capabilities">{endpoint.capabilities.map(capability =>
          <li key={capability.code}><span>{humanize(capability.code)}</span><Badge className="tone-neutral">{capability.access === 'readOnly' ? 'Read only' : 'Unsupported'}</Badge></li>)}</ul>
        <h3>Latest measurements</h3>
        <dl className="device-readings">{endpoint.measurements.map(reading => <div key={reading.metric}>
          <dt>{metricLabels[reading.metric]}</dt><dd>{formatReading(reading)} {reading.quality === 'stale' && <Badge className="tone-warn">Stale</Badge>}</dd>
        </div>)}</dl>
      </Card>)}
      <DeviceAudit property={property} device={device.id} />
    </>}
  </>
}
