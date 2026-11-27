import { useMemo, useState } from 'react'
import { useQueries } from '@tanstack/react-query'
import { Area, CartesianGrid, ComposedChart, Line, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { api } from '../shared/api'
import type { Metric, Point, Property, PropertyState } from '../shared/types'
import { Card } from '../shared/ui'

/**
 * A chart curve: one endpoint's series, the installation series (no endpoint), or the sum of several endpoints' series (`endpoints`,
 * for example both Growatt inverters' PV input).
 */
type Curve = { metric: Metric; label: string; color: string; endpoint?: number; endpoints?: number[] }

/**
 * Sums recorded series of separate meters at the times all of them reported. A point of the first series is paired with the nearest
 * point of every other series within `toleranceSeconds`; when any series has no point that close, that time is left out, so a
 * missing inverter shows as a gap instead of a lower total. The sum takes the latest of the paired times.
 */
export function sumSeries(series: Point[][], toleranceSeconds: number): Point[] {
  if (series.length === 0) return []
  const sorted = series.map(points => points
    .filter(point => point.number !== null && Number.isFinite(point.number) && Number.isFinite(Date.parse(point.observedAt)))
    .map(point => ({ time: Date.parse(point.observedAt), number: point.number! }))
    .sort((left, right) => left.time - right.time))
  const tolerance = toleranceSeconds * 1000
  const used = sorted.map(() => new Set<number>())
  const result: Point[] = []
  for (const anchor of sorted[0]) {
    const matches = sorted.slice(1).map((points, index) => {
      let best = -1
      for (let at = 0; at < points.length; at++) {
        if (used[index + 1].has(at) || Math.abs(points[at].time - anchor.time) > tolerance) continue
        if (best < 0 || Math.abs(points[at].time - anchor.time) < Math.abs(points[best].time - anchor.time)) best = at
      }
      return best
    })
    if (matches.some(match => match < 0)) continue
    matches.forEach((match, index) => used[index + 1].add(match))
    const time = Math.max(anchor.time, ...matches.map((match, index) => sorted[index + 1][match].time))
    const number = anchor.number + matches.reduce((sum, match, index) => sum + sorted[index + 1][match].number, 0)
    result.push({ observedAt: new Date(time).toISOString(), number, flag: null, code: null })
  }
  return result
}
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
  // One request per series; a summed curve requests each of its endpoints.
  const requests = curves.flatMap((curve, index) => (curve.endpoints ?? [curve.endpoint]).map(endpoint => ({ index, endpoint, metric: curve.metric })))
  const queries = useQueries({ queries: requests.map(({ metric, endpoint }) => ({
    queryKey: ['property', property.id, 'history', metric, hours, endpoint ?? 'installation'],
    queryFn: ({ signal }: { signal: AbortSignal }) => api.history(property.id, metric, hours, endpoint, signal),
    refetchInterval: 15000,
  })) })
  const series = curves.map((curve, index) => {
    const points = requests.flatMap((request, at) => request.index === index ? [queries[at].data ?? []] : [])
    return { metric: curve.metric, points: curve.endpoints ? sumSeries(points, pollSeconds / 2) : points[0] ?? [] }
  })
  return <HistoryChart property={property} title={title} curves={curves} hours={hours} pollSeconds={pollSeconds}
    unit={unit} action={action} pending={queries.some(query => query.isPending)} failed={queries.some(query => query.isError)}
    series={series} />
}

export function HistoryChart({ property, title, curves, hours, pollSeconds, unit, action, pending, failed, series,
  minimumPoints = 1, window }: {
  property: Property; title: string; curves: Curve[]; hours: number; pollSeconds: number; unit: string
  action?: React.ReactNode; pending: boolean; failed: boolean; series: Parameters<typeof mergeHistory>[0]
  minimumPoints?: number; window?: { from: string; to: string }
}) {
  const points = mergeHistory(series, pollSeconds)
  const ready = curves.some(curve => points.filter(point => typeof point[curve.metric] === 'number').length >= minimumPoints)
  const showSeconds = points.length > 0 && points.at(-1)!.time - points[0].time < 5 * 60 * 1000
  const formatter = new Intl.DateTimeFormat(undefined, {
    ...(window && hours > 24 ? { month: 'short', day: 'numeric' } as const : {}),
    hour: '2-digit', minute: '2-digit', second: showSeconds ? '2-digit' : undefined, timeZone: property.timeZone,
  })
  const time = (value: number) => formatter.format(value)
  const range = hours === 1 ? 'last hour' : hours > 24 && window ? `last ${hours / 24} days` : `last ${hours} hours`
  const headingRange = range[0].toUpperCase() + range.slice(1)
  const domain: [number | 'dataMin', number | 'dataMax'] = window ? [Date.parse(window.from), Date.parse(window.to)] : ['dataMin', 'dataMax']

  return <Card className="trend-card">
    <div className="card-heading">
      <div><h2>{title}</h2><p>{headingRange} · {unit}</p></div>
      {action}
    </div>
    <ul className="chart-legend">{curves.map(curve => <li key={curve.metric}><span style={{ background: curve.color }} />{curve.label}</li>)}</ul>
    {failed && <p role="alert" className="muted">Some historical readings are unavailable.</p>}
    {ready ? <div className="chart" role="img" aria-label={`${title}, ${range}`}>
      <ResponsiveContainer width="100%" height="100%" minWidth={0}>
        <ComposedChart data={points} margin={{ left: -12, right: 12, top: 10, bottom: 0 }}>
          <CartesianGrid vertical={false} stroke="#e5eaf0" strokeDasharray="4 4" />
          <XAxis type="number" dataKey="time" domain={domain} tickFormatter={time} minTickGap={28} tickLine={false} axisLine={false} tick={{ fill: '#5f6c7e', fontSize: 11 }} />
          <YAxis unit={unit === 'W' ? '' : unit} tickLine={false} axisLine={false} tick={{ fill: '#5f6c7e', fontSize: 11 }} />
          <Tooltip labelFormatter={value => time(Number(value))} formatter={(value, name) => [`${value} ${unit}`, name]} />
          {curves.map((curve, index) => index === 0
            ? <Area key={curve.metric} name={curve.label} dataKey={curve.metric} stroke={curve.color} fill={curve.color}
              fillOpacity={0.08} strokeWidth={2} type="linear" dot={window ? { r: 2 } : false} isAnimationActive={false} connectNulls={false} />
            : <Line key={curve.metric} name={curve.label} dataKey={curve.metric} stroke={curve.color}
              strokeWidth={2} dot={window ? { r: 2 } : false} isAnimationActive={false} connectNulls={false} />)}
        </ComposedChart>
      </ResponsiveContainer>
    </div> : <div className="chart-empty" role="status">{pending ? 'Loading readings…' : 'No provider readings were recorded in this range.'}</div>}
    {ready && <div className="chart-summary"><span>Latest recorded</span>{curves.map(curve => {
      const value = points.findLast(point => typeof point[curve.metric] === 'number')?.[curve.metric]
      return <span key={curve.metric}><strong>{value == null ? '—' : `${value.toLocaleString()} ${unit}`}</strong> {curve.label}</span>
    })}</div>}
  </Card>
}

/**
 * The solar and consumption curves. An installation series wins when the provider records one. Growatt V4 records none: it
 * records each inverter's PV input power and AC output, so the curves then sum the inverters' own recorded series (the same
 * measurements the Live Growatt summary adds up). Readings stay raw here, as in History.
 */
export function productionCurves(state?: PropertyState): Curve[] {
  const site = (metric: Metric) => state?.installation.some(reading => reading.metric === metric && reading.quality !== 'missing') ?? false
  const endpoints = (metric: Metric) => (state?.devices ?? []).filter(device => device.kind === 'inverter')
    .flatMap(device => device.endpoints).filter(endpoint => endpoint.measurements.some(reading => reading.metric === metric))
    .map(endpoint => endpoint.id)
  const pv = endpoints('pvPower')
  const output = endpoints('activePower')
  return [
    site('pvPower') || pv.length === 0 ? production[0]
      : { metric: 'pvPower', label: pv.length > 1 ? 'PV production (sum of inverters)' : 'PV production', color: production[0].color, endpoints: pv },
    site('householdPower') || output.length === 0 ? production[1]
      : { metric: 'activePower', label: output.length > 1 ? 'Inverter output (sum of inverters)' : 'Inverter output', color: production[1].color,
        endpoints: output },
  ]
}

/**
 * Recent provider readings only: solar and consumption power (the installation's series, or the inverters' recorded series summed
 * at matching times), and the battery power of the device that reports it. Longer ranges and energy are on the History page.
 */
export function Energy({ property, state, pollSeconds }: { property: Property; state?: PropertyState; pollSeconds: number }) {
  const [hours, setHours] = useState(1)
  const curveKey = JSON.stringify(productionCurves(state))
  const curves = useMemo<Curve[]>(() => JSON.parse(curveKey), [curveKey])
  const battery = state?.devices.flatMap(device => device.endpoints)
    .find(endpoint => endpoint.capabilities.some(capability => capability.code === 'batteryPower'))?.id
  const batteryCurves = useMemo<Curve[]>(() => [{ metric: 'batteryPower', label: 'Battery power', color: '#117d80', endpoint: battery }], [battery])

  return <>
    <div className="page-heading page-heading-inline"><div><h1>Energy</h1><p>Recent solar production, household demand and battery power
      recorded from your providers. Use History for longer ranges and energy totals.</p></div>
      <select className="input select-input" aria-label="History range" value={hours} onChange={event => setHours(Number(event.target.value))}>
        <option value={1}>Last hour</option><option value={6}>Last 6 hours</option>
      </select>
    </div>
    <div className="energy-grid">
      <TrendCard property={property} title="Solar and household power" hours={hours} pollSeconds={pollSeconds} curves={curves} />
      {battery !== undefined && <TrendCard property={property} title="Battery power" hours={hours} pollSeconds={pollSeconds} curves={batteryCurves} />}
    </div>
    {state && battery === undefined && <p className="muted">No connected device reports battery power.</p>}
  </>
}
