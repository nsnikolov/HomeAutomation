import { useMemo, useState } from 'react'
import { useQueries } from '@tanstack/react-query'
import { Area, CartesianGrid, ComposedChart, Line, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { api } from '../shared/api'
import type { Metric, Point, Property, PropertyState } from '../shared/types'
import { Card } from '../shared/ui'

type Curve = { metric: Metric; label: string; color: string; endpoint?: number }
const production: Curve[] = [
  { metric: 'pvPower', label: 'PV production', color: '#11846d' },
  { metric: 'householdPower', label: 'Consumption', color: '#5373c7' },
]

type PlotPoint = { time: number } & Partial<Record<Metric, number | null>>

export function mergeHistory(series: { metric: Metric; points: Point[] }[], cadence: number): PlotPoint[] {
  const all = new Map<number, PlotPoint>()
  for (const { metric, points } of series) {
    const sorted = points.toSorted((left, right) => Date.parse(left.observedAt) - Date.parse(right.observedAt))
    let previous: number | undefined
    for (const point of sorted) {
      const time = Date.parse(point.observedAt)
      if (!Number.isFinite(time)) continue
      if (previous !== undefined && time - previous > cadence * 2200) {
        const gap = previous + cadence * 1000
        all.set(gap, { ...all.get(gap), time: gap, [metric]: null })
      }
      all.set(time, { ...all.get(time), time, [metric]: point.number })
      previous = time
    }
  }
  return [...all.values()].sort((left, right) => left.time - right.time)
}

export function TrendCard({ property, title, curves = production, hours = 1, pollSeconds, unit = 'W', action }: {
  property: Property; title: string; curves?: Curve[]; hours?: number; pollSeconds: number; unit?: string; action?: React.ReactNode
}) {
  const queries = useQueries({ queries: curves.map(curve => ({
    queryKey: ['property', property.id, 'history', curve.metric, hours, curve.endpoint ?? 'installation'],
    queryFn: ({ signal }: { signal: AbortSignal }) => api.history(property.id, curve.metric, hours, curve.endpoint, signal),
    refetchInterval: 15000,
  })) })
  return <HistoryChart property={property} title={title} curves={curves} hours={hours} pollSeconds={pollSeconds}
    unit={unit} action={action} pending={queries.some(query => query.isPending)} failed={queries.some(query => query.isError)}
    series={queries.map((query, index) => ({
      metric: curves[index].metric, points: query.data ?? [],
    }))} />
}

export function HistoryChart({ property, title, curves, hours, pollSeconds, unit, action, pending, failed, series,
  minimumPoints = 1 }: {
  property: Property; title: string; curves: Curve[]; hours: number; pollSeconds: number; unit: string
  action?: React.ReactNode; pending: boolean; failed: boolean; series: Parameters<typeof mergeHistory>[0]
  minimumPoints?: number
}) {
  const points = mergeHistory(series, pollSeconds)
  const ready = curves.some(curve => points.filter(point => typeof point[curve.metric] === 'number').length >= minimumPoints)
  const showSeconds = points.length > 0 && points.at(-1)!.time - points[0].time < 5 * 60 * 1000
  const formatter = new Intl.DateTimeFormat(undefined, {
    hour: '2-digit', minute: '2-digit', second: showSeconds ? '2-digit' : undefined, timeZone: property.timeZone,
  })
  const time = (value: number) => formatter.format(value)
  const range = hours === 1 ? 'last hour' : `last ${hours} hours`

  return <Card className="trend-card">
    <div className="card-heading">
      <div><h2>{title}</h2><p>{hours === 1 ? 'Last hour' : `Last ${hours} hours`} · {unit}</p></div>
      {action}
    </div>
    <ul className="chart-legend">{curves.map(curve => <li key={curve.metric}><span style={{ background: curve.color }} />{curve.label}</li>)}</ul>
    {failed && <p role="alert" className="muted">Some historical readings are unavailable.</p>}
    {ready ? <div className="chart" role="img" aria-label={`${title}, ${range}`}>
      <ResponsiveContainer width="100%" height="100%" minWidth={0}>
        <ComposedChart data={points} margin={{ left: -12, right: 12, top: 10, bottom: 0 }}>
          <CartesianGrid vertical={false} stroke="#e5eaf0" strokeDasharray="4 4" />
          <XAxis type="number" dataKey="time" domain={['dataMin', 'dataMax']} tickFormatter={time} minTickGap={28} tickLine={false} axisLine={false} tick={{ fill: '#5f6c7e', fontSize: 11 }} />
          <YAxis unit={unit === 'W' ? '' : unit} tickLine={false} axisLine={false} tick={{ fill: '#5f6c7e', fontSize: 11 }} />
          <Tooltip labelFormatter={value => time(Number(value))} formatter={(value, name) => [`${value} ${unit}`, name]} />
          {curves.map((curve, index) => index === 0
            ? <Area key={curve.metric} name={curve.label} dataKey={curve.metric} stroke={curve.color} fill={curve.color}
              fillOpacity={0.08} strokeWidth={2} type="linear" dot={false} isAnimationActive={false} connectNulls={false} />
            : <Line key={curve.metric} name={curve.label} dataKey={curve.metric} stroke={curve.color}
              strokeWidth={2} dot={false} isAnimationActive={false} connectNulls={false} />)}
        </ComposedChart>
      </ResponsiveContainer>
    </div> : <div className="chart-empty" role="status">{pending ? 'Loading readings…' : 'Waiting for recorded readings.'}</div>}
    {ready && <div className="chart-summary"><span>Latest recorded</span>{curves.map(curve => {
      const value = points.findLast(point => typeof point[curve.metric] === 'number')?.[curve.metric]
      return <span key={curve.metric}><strong>{value == null ? '—' : `${value.toLocaleString()} ${unit}`}</strong> {curve.label}</span>
    })}</div>}
  </Card>
}

export function Energy({ property, state, pollSeconds }: { property: Property; state?: PropertyState; pollSeconds: number }) {
  const [hours, setHours] = useState(1)
  const meter = state?.devices.find(device => device.kind === 'smartSwitch')?.endpoints[0]?.id
  const battery = state?.devices.find(device => device.kind === 'battery')?.endpoints[0]?.id
  const batteryCurves = useMemo<Curve[]>(() => [{ metric: 'batteryPower', label: 'Battery power', color: '#117d80', endpoint: battery }], [battery])
  const meterCurves = useMemo<Curve[]>(() => [{ metric: 'energy', label: 'Heater meter counter', color: '#5373c7', endpoint: meter }], [meter])

  return <>
    <div className="page-heading page-heading-inline"><div><h1>Energy</h1><p>Historical solar production, household demand and device readings.</p></div>
      <select className="input select-input" aria-label="History range" value={hours} onChange={event => setHours(Number(event.target.value))}>
        <option value={1}>Last hour</option><option value={6}>Last 6 hours</option>
      </select>
    </div>
    <div className="energy-grid">
      <TrendCard property={property} title="Solar and household power" hours={hours} pollSeconds={pollSeconds} />
      {battery !== undefined && <TrendCard property={property} title="Battery power" hours={hours} pollSeconds={pollSeconds} curves={batteryCurves} />}
      {meter !== undefined && <TrendCard property={property} title="Water heater energy counter" hours={hours} pollSeconds={pollSeconds} unit="kWh" curves={meterCurves} />}
    </div>
  </>
}
