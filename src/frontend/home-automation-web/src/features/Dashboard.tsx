import { Activity, ArrowRight, BatteryCharging, Bell, House, PlugZap, Sun, Thermometer, type LucideIcon } from 'lucide-react'
import { Link } from 'react-router'
import type { Health, Metric, Property, PropertyState, Reading } from '../shared/types'
import { Badge, Card } from '../shared/ui'
import { formatMoment } from '../shared/utils'
import { DeviceBadge, DeviceIcon, formatReading } from './Devices'
import { TrendCard } from './Energy'
import { DeviceCardOrder, useDevicePreferences } from './DeviceCardOrder'

function pick(readings: Reading[] | undefined, metric: Metric) {
  return readings?.find(reading => reading.metric === metric)
}

function MetricCard({ title, icon: Icon, tone, reading, description }: {
  title: string; icon: LucideIcon; tone: string; reading?: Reading; description: string
}) {
  const current = reading?.quality === 'fresh'
  const watts = reading?.number != null && reading.unit === 'W'
  const value = current && watts ? `${(reading.number! / 1000).toFixed(2)} kW` : current ? formatReading(reading) : '—'

  return <Card className="metric-card">
    <span className={`metric-icon ${tone}`}><Icon size={20} aria-hidden /></span>
    <h2>{title}</h2>
    <strong className="metric-value">{value}</strong>
    <p>{reading?.quality === 'stale' ? 'Reading is stale' : description}</p>
  </Card>
}

export function Dashboard({ property, state, health, loading }: {
  property: Property; state?: PropertyState; health?: Health; loading: boolean
}) {
  const battery = state?.devices.find(device => device.kind === 'battery')?.endpoints[0]?.measurements
  const sensors = (state?.devices ?? []).filter(device => device.kind === 'sensor' || device.kind === 'airConditioner')
  const root = `/properties/${property.id}`
  const preferences = useDevicePreferences(property.id, 'dashboard')

  return <>
    <div className="page-heading page-heading-inline">
      <div><h1>Dashboard</h1><p>Energy and device overview for {property.name}.</p></div>
      {health && <Badge className={health.database === 'healthy' && health.simulator === 'running' ? 'tone-good' : 'tone-warn'}>
        <Activity size={14} />{health.database === 'healthy' ? `Ingestion ${health.simulator}` : 'Database unavailable'}
      </Badge>}
    </div>
    {loading && <p className="sr-only" role="status">Loading latest readings.</p>}
    <div className="metric-grid">
      <MetricCard title="PV production" icon={Sun} tone="accent-solar" reading={pick(state?.installation, 'pvPower')} description="Parallel array output" />
      <MetricCard title="Battery charge" icon={BatteryCharging} tone="accent-teal" reading={pick(battery, 'batterySoc')} description="Shared battery state of charge" />
      <MetricCard title="Grid availability" icon={PlugZap} tone="accent-blue" reading={pick(state?.installation, 'gridAvailable')} description="AC input status" />
      <MetricCard title="Household load" icon={House} tone="accent-purple" reading={pick(state?.installation, 'householdPower')} description="Whole-property consumption" />
    </div>

    <div className="dashboard-grid">
      <TrendCard property={property} title="Power over time" pollSeconds={health?.pollSeconds ?? 10}
        action={<Link className="text-link" to={root + '/energy'}>Energy <ArrowRight size={15} /></Link>} />
      <Card className="panel">
        <div className="card-heading"><div><h2>Recent alerts</h2><p>Device faults and offline events</p></div><span className="panel-icon"><Bell size={18} /></span></div>
        {state?.alerts.length ? <ul className="rows">{state.alerts.slice(0, 5).map(alert =>
          <li key={`${alert.deviceId}:${alert.occurredAt}:${alert.code}`}><div><strong>{alert.deviceName}</strong>
            <p>{alert.code === 'SIM_METER_FAULT' ? 'Meter fault' : alert.code === 'DEVICE_OFFLINE' ? 'Device offline' : alert.code}</p>
          </div><time dateTime={alert.occurredAt}>{formatMoment(alert.occurredAt, property.timeZone)}</time></li>)}</ul>
          : <p className="empty-note">No recent device alerts.</p>}
      </Card>
      <Card className="panel">
        <div className="card-heading"><div><h2>Device status</h2><p>{state?.devices.length ?? 0} monitored devices</p></div>
          <Link className="text-link" to={root + '/devices'}>View devices <ArrowRight size={15} /></Link></div>
        {preferences.error && <p className="muted" role="alert">{preferences.error}</p>}
        <DeviceCardOrder rows items={state?.devices ?? []} group="assigned" preferences={preferences}>{device => <>
            <Link to={`${root}/devices/${device.id}`}><span className="device-icon"><DeviceIcon kind={device.kind} size={17} /></span><strong>{device.name}</strong></Link>
            <DeviceBadge device={device} />
          </>}</DeviceCardOrder>
      </Card>
      <Card className="panel">
        <div className="card-heading"><div><h2>Temperatures</h2><p>Latest indoor readings</p></div><span className="panel-icon"><Thermometer size={18} /></span></div>
        <ul className="rows">{sensors.map(sensor => {
          const reading = pick(sensor.endpoints[0]?.measurements, 'temperature')
          return <li key={sensor.id}><div><strong>{sensor.name}</strong><p>{reading?.quality === 'stale' ? 'Stale reading' : 'Room temperature'}</p></div>
            <span className="temperature">{formatReading(reading)}</span></li>
        })}</ul>
        {sensors.length === 0 && <p className="empty-note">No temperature readings are available.</p>}
      </Card>
    </div>
  </>
}
