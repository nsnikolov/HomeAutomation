import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ArrowLeft, ArrowUpRight, Battery, Cable, CircuitBoard, Search, Snowflake, Sun, Thermometer } from 'lucide-react'
import { Link, useParams } from 'react-router'
import { api } from '../shared/api'
import type { Device, Metric, Property, Reading } from '../shared/types'
import { Badge, Card, Input } from '../shared/ui'
import { formatMoment, humanize } from '../shared/utils'
import { DeviceCardOrder, useDevicePreferences } from './DeviceCardOrder'

export const metricLabels: Record<Metric, string> = {
  pvPower: 'PV production', householdPower: 'Household consumption', gridAvailable: 'Grid availability',
  batterySoc: 'Battery charge', batteryPower: 'Battery power', voltage: 'Voltage', current: 'Current',
  activePower: 'Active power', energy: 'Energy counter', temperature: 'Temperature', humidity: 'Relative humidity',
  switchOn: 'Switch state', targetTemperature: 'Target temperature', operatingMode: 'Operating mode',
  gridVoltage: 'Grid voltage', gridFrequency: 'Grid frequency', gridImportPower: 'Grid import',
  pvVoltage: 'PV voltage', warningStatus: 'Reported warning',
  acOutputVoltage: 'AC output voltage', inverterLoadPercent: 'Inverter load', acChargingPower: 'AC charging power',
  outdoorTemperature: 'Outdoor temperature', fanSpeed: 'Fan speed',
  leavingWaterTemperature: 'Leaving water', targetLeavingWaterTemperature: 'Water target',
  heatingControlMode: 'Heating control', waterSetpointMode: 'Water setpoint mode',
  heatingWarning: 'Heating warning', heatingEmergency: 'Heating emergency',
}

export function DeviceIcon({ kind, size = 18 }: { kind: string; size?: number }) {
  const Icon = ({ inverter: Sun, battery: Battery, airConditioner: Snowflake, heatPump: Thermometer, smartSwitch: Cable, sensor: Thermometer })[kind] ?? CircuitBoard
  return <Icon size={size} aria-hidden />
}

export function DeviceBadge({ device }: { device: Device }) {
  const status = device.faultCode ? 'Fault' : device.connectivity === 'offline' ? 'Offline'
    : device.quality === 'stale' ? 'Stale' : device.connectivity === 'online' ? 'Online' : 'Unknown'
  const tone = ({ Fault: 'bad', Offline: 'neutral', Unknown: 'neutral', Stale: 'warn', Online: 'good' })[status]
  return <Badge className={`tone-${tone}`}><i />{status}</Badge>
}

export function formatReading(reading: Reading | undefined) {
  if (!reading || reading.quality === 'missing') return '—'
  if (reading.flag !== null) {
    if (reading.metric === 'gridAvailable') return reading.flag ? 'Available' : 'Outage'
    return reading.flag ? 'On' : 'Off'
  }
  if (reading.number !== null) {
    const value = new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 }).format(reading.number)
    return reading.unit === '%' ? `${value}%` : `${value}${reading.unit ? ' ' + reading.unit : ''}`
  }
  return reading.code ?? '—'
}

export function Devices({ property, devices }: { property: Property; devices?: Device[] }) {
  const [search, setSearch] = useState('')
  const [kind, setKind] = useState('')
  const preferences = useDevicePreferences(property.id, 'devices')
  const rooms = useQuery({ queryKey: ['property', property.id, 'rooms'], queryFn: ({ signal }) => api.rooms(property.id, signal) })
  const matches = (device: Device) => (!kind || device.kind === kind) &&
    `${device.name} ${device.manufacturer} ${device.model}`.toLowerCase().includes(search.trim().toLowerCase())
  const groups = [...(rooms.data ?? []), { id: null, name: 'Unassigned room' }]

  return <>
    <div className="page-heading"><h1>Devices</h1><p>Monitor device state, readings and available capabilities.</p></div>
    <div className="filters">
      <label className="search"><Search size={17} aria-hidden /><Input placeholder="Search devices" aria-label="Search devices" value={search}
        onChange={event => setSearch(event.target.value)} /></label>
      <select className="input select-input" aria-label="Device type" value={kind} onChange={event => setKind(event.target.value)}>
        <option value="">All device types</option>
        {[...new Set((devices ?? []).map(device => device.kind))].map(item => <option key={item} value={item}>{humanize(item)}</option>)}
      </select>
    </div>
    {(!devices || rooms.isPending) && <p role="status">Loading devices…</p>}
    {rooms.isError && <p role="alert">Rooms could not be loaded.</p>}
    {devices && !devices.some(matches) && <Card className="placeholder">No devices match your filters.</Card>}
    {preferences.error && <p className="muted" role="alert">{preferences.error}</p>}
    {groups.map(room => {
      const items = (devices ?? []).filter(device => device.roomId === room.id)
      if (!items.some(matches)) return null
      return <section className="room" key={room.id ?? 'unassigned'}>
        <h2>{room.name}</h2>
        <DeviceCardOrder items={items} group={room.id === null ? 'unassigned' : `room-${room.id}`}
          preferences={preferences} visible={matches}>{device => <DeviceCard device={device} property={property} />}</DeviceCardOrder>
      </section>
    })}
  </>
}

function DeviceCard({ device, property }: { device: Device; property: Property }) {
  const readings = device.endpoints.flatMap(endpoint => endpoint.measurements).slice(0, 3)
  return <Card className="device-card">
    <div className="device-card-top">
      <span className="device-icon"><DeviceIcon kind={device.kind} /></span><DeviceBadge device={device} />
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
      <div className="page-heading"><h1>{device.name}</h1><p>{device.manufacturer} · {device.model}</p></div>
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
    </>}
  </>
}
