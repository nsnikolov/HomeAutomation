import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import type { Property, SensorHistory as History, SensorPeriod } from '../shared/types'
import { HistoryChart } from './Energy'
import { metricLabels } from './Devices'

const hours = { '24h': 24, '7d': 168, '30d': 720 }

export function SensorHistory({ property, device, load, source }: {
  property: Property; device: string; source: string;
  load: (property: number, device: string, period: SensorPeriod, signal: AbortSignal) => Promise<History>;
}) {
  const [period, setPeriod] = useState<SensorPeriod>('24h')
  const query = useQuery({
    queryKey: ['property', property.id, source, 'sensor-history', device, period],
    queryFn: ({ signal }) => load(property.id, device, period, signal), refetchInterval: 60_000,
  })
  const data = query.data
  return <div className="sensor-history">
    <div className="filters">
      <label>History period <select className="input select-input" value={period}
        onChange={event => setPeriod(event.target.value as SensorPeriod)}>
        <option value="24h">24 hours</option>
        <option value="7d">7 days</option>
        <option value="30d">30 days</option>
      </select></label>
      <span className="muted">{property.timeZone}</span>
    </div>
    {query.isPending && <p role="status">Loading sensor history…</p>}
    {query.isError && <p role="alert">Sensor history is unavailable.</p>}
    {data && !data.series.some(series => series.points.length > 0) &&
      <p className="muted">No timestamped temperature or humidity readings in this period.</p>}
    {data?.series.filter(series => series.points.length > 0).map(series => {
      const label = metricLabels[series.metric]
      const title = data.series.filter(channel => channel.metric === series.metric).length > 1 ? `${label} · ${series.code}` : label
      return <div key={series.code}>
        <HistoryChart property={property} title={title} hours={hours[period]} pollSeconds={data.pollSeconds} unit={series.unit}
          curves={[{ metric: series.metric, label, color: series.metric === 'temperature' ? '#a9442e' : '#315b98' }]}
          series={[series]} pending={false} failed={false} window={data} />
        {series.truncated && <p className="muted">Showing the most recent readings in this period.</p>}
      </div>
    })}
  </div>
}
