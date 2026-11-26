import { cloneElement, createContext, useContext, type ReactElement } from 'react'
import {
  Area, Bar, BarChart, CartesianGrid, Cell, ComposedChart, Line, LineChart, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis,
} from 'recharts'
import type { MetricAggregation } from '../../shared/types'
import {
  formatHours, formatInstant, formatPercent, formatValue, incompleteBelow, markerBucket, qualityHelp, qualityLabels,
  type BucketRow, type RawRow, type ReplacementMarker, type Summary,
} from './historyModel'

/** Tests and print layouts can fix the chart size; the application measures its container. */
export const ChartSizeContext = createContext<{ width: number; height: number } | null>(null)
export const seriesStyles = [
  { color: '#0b705d', dash: undefined, name: 'solid' },
  { color: '#5373c7', dash: '7 4', name: 'dashed' },
  { color: '#b4531c', dash: '2 3', name: 'dotted' },
  { color: '#7a4fb3', dash: '12 4 2 4', name: 'dash-dot' },
]
const axis = { fill: '#5f6c7e', fontSize: 11 }

function ChartBox({ label, height = 260, children }: { label: string; height?: number; children: ReactElement<{ width?: number; height?: number }> }) {
  const fixed = useContext(ChartSizeContext)
  return <div className="history-chart" role="img" aria-label={label} style={{ height: fixed?.height ?? height }}>
    {fixed ? cloneElement(children, { width: fixed.width, height: fixed.height })
      : <ResponsiveContainer width="100%" height="100%" minWidth={0}>{children}</ResponsiveContainer>}
  </div>
}

function Patterns() {
  return <defs>
    <pattern id="history-hatch" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
      <rect width="6" height="6" fill="#dbe5f7" /><line x1="0" y1="0" x2="0" y2="6" stroke="#5373c7" strokeWidth="2.5" />
    </pattern>
    <pattern id="history-unknown" width="5" height="5" patternUnits="userSpaceOnUse">
      <rect width="5" height="5" fill="#f2f4f7" /><circle cx="2.5" cy="2.5" r="0.9" fill="#8b96a6" />
    </pattern>
  </defs>
}

export type ComparisonSeries = { key: string; name: string; unit: string | null; rows: BucketRow[] }

/** Average line with a min/max band for the primary series; comparison series add their own averages on the same axis. */
export function MeasurementChart({ series, markers, label }: { series: ComparisonSeries[]; markers: ReplacementMarker[]; label: string }) {
  const primary = series[0]
  const keys = new Map<string, Record<string, unknown>>()
  series.forEach((item, index) => item.rows.forEach(row => {
    const entry = keys.get(row.key) ?? { key: row.key, label: row.label, title: row.title, start: row.start }
    entry[`average${index}`] = row.average
    entry[`row${index}`] = row
    if (index === 0) entry.band = row.band
    keys.set(row.key, entry)
  }))
  const data = [...keys.values()].sort((left, right) => Number(left.start) - Number(right.start))
  const sparse = data.length <= 48
  return <ChartBox label={label}>
    <ComposedChart data={data} margin={{ left: 4, right: 14, top: 12, bottom: 0 }}>
      <CartesianGrid vertical={false} stroke="#e5eaf0" strokeDasharray="4 4" />
      <XAxis dataKey="label" minTickGap={24} tickLine={false} axisLine={false} tick={axis} />
      <YAxis tickLine={false} axisLine={false} tick={axis} width={64} domain={['auto', 'auto']}
        tickFormatter={value => formatValue(Number(value), primary.unit)} label={{ value: primary.unit ?? '', angle: -90, position: 'insideLeft', fill: '#5f6c7e', fontSize: 11 }} />
      <Tooltip content={({ active, payload }) => active && payload?.length
        ? <BucketTooltip entries={series.map((item, index) => ({ name: item.name, unit: item.unit,
          row: (payload[0].payload as Record<string, unknown>)[`row${index}`] as BucketRow | undefined }))} /> : null} />
      {series.length === 1 && <Area dataKey="band" name="Minimum to maximum" stroke="none" fill={seriesStyles[0].color} fillOpacity={0.12}
        isAnimationActive={false} connectNulls={false} />}
      {series.map((item, index) => <Line key={item.key} dataKey={`average${index}`} name={item.name} stroke={seriesStyles[index].color}
        strokeDasharray={seriesStyles[index].dash} strokeWidth={2} connectNulls={false} isAnimationActive={false}
        dot={sparse ? { r: 3 } : false} activeDot={{ r: 4 }} />)}
      {markers.map(marker => {
        const x = markerBucket(primary.rows, marker.at)
        return x ? <ReferenceLine key={marker.key} x={x} stroke="#805108" strokeDasharray="3 3"
          label={{ value: 'Replaced', position: 'insideTopRight', fill: '#805108', fontSize: 11 }} /> : null
      })}
    </ComposedChart>
  </ChartBox>
}

function BucketTooltip({ entries }: { entries: { name: string; unit: string | null; row?: BucketRow }[] }) {
  const first = entries.find(entry => entry.row)?.row
  if (!first) return null
  return <div className="history-tooltip">
    <strong>{first.title}</strong>
    {entries.map(({ name, unit, row }) => row && <div key={name}>
      {entries.length > 1 && <span className="history-tooltip-name">{name}</span>}
      {row.average !== null || row.measuredKwh !== null || row.estimatedKwh !== null || row.onHours !== null ? <>
        {row.average !== null && <div>Average {formatValue(row.average, unit)}</div>}
        {row.minimum !== null && row.maximum !== null && <div>Minimum {formatValue(row.minimum, unit)} · Maximum {formatValue(row.maximum, unit)}</div>}
        {row.measuredKwh !== null && <div>Measured {formatValue(row.measuredKwh, 'kWh')}{row.measuredPartial ? ' (partial)' : ''}</div>}
        {row.estimatedKwh !== null && <div>Estimated {formatValue(row.estimatedKwh, 'kWh')}{row.estimatedPartial ? ' (partial)' : ''}</div>}
        {row.onHours !== null && <div>ON {formatHours(row.onHours * 3600)} of {formatHours(row.bucket.coveredSeconds)} covered</div>}
      </> : null}
      <div className="muted">{qualityLabels[row.quality]}{row.quality !== 'noData' && row.quality !== 'notAggregated' && row.quality !== 'notReported'
        ? ` · ${formatPercent(row.coverage)} covered` : ''}</div>
    </div>)}
  </div>
}

export type EnergySeries = { key: string; name: string; rows: BucketRow[]; basis: 'measured' | 'estimated' }

/**
 * Interval energy as bars. Measured counter energy is solid; energy estimated from power is hatched. Series are drawn side
 * by side and never stacked or added. Partial intervals are translucent with a dashed outline.
 */
export function EnergyChart({ series, markers, label }: { series: EnergySeries[]; markers: ReplacementMarker[]; label: string }) {
  const keys = new Map<string, Record<string, unknown>>()
  series.forEach((item, index) => item.rows.forEach(row => {
    const entry = keys.get(row.key) ?? { key: row.key, label: row.label, start: row.start }
    entry[`value${index}`] = item.basis === 'measured' ? row.measuredKwh : row.estimatedKwh
    entry[`negative${index}`] = item.basis === 'estimated' ? row.estimatedNegativeKwh : null
    entry[`row${index}`] = row
    keys.set(row.key, entry)
  }))
  const data = [...keys.values()].sort((left, right) => Number(left.start) - Number(right.start))
  const signed = data.some(entry => series.some((_, index) => typeof entry[`negative${index}`] === 'number'))
  return <ChartBox label={label}>
    <BarChart data={data} margin={{ left: 4, right: 14, top: 12, bottom: 0 }} barGap={2}>
      <Patterns />
      <CartesianGrid vertical={false} stroke="#e5eaf0" strokeDasharray="4 4" />
      <XAxis dataKey="label" minTickGap={24} tickLine={false} axisLine={false} tick={axis} />
      <YAxis tickLine={false} axisLine={false} tick={axis} width={64} tickFormatter={value => formatValue(Number(value), 'kWh')} />
      {signed && <ReferenceLine y={0} stroke="#8b96a6" />}
      <Tooltip content={({ active, payload }) => active && payload?.length
        ? <BucketTooltip entries={series.map((item, index) => ({ name: item.name, unit: 'kWh',
          row: (payload[0].payload as Record<string, unknown>)[`row${index}`] as BucketRow | undefined }))} /> : null} />
      {series.flatMap((item, index) => {
        const fill = item.basis === 'measured' ? seriesStyles[index].color : 'url(#history-hatch)'
        const bars = [<Bar key={`${item.key}-value`} dataKey={`value${index}`} name={item.name} fill={fill} isAnimationActive={false}>
          {data.map(entry => {
            const row = entry[`row${index}`] as BucketRow | undefined
            const partial = item.basis === 'measured' ? row?.measuredPartial : row?.estimatedPartial
            return <Cell key={String(entry.key)} fillOpacity={partial ? 0.45 : 1} stroke={partial ? '#805108' : undefined}
              strokeDasharray={partial ? '3 2' : undefined} />
          })}
        </Bar>]
        if (signed && item.basis === 'estimated')
          bars.push(<Bar key={`${item.key}-negative`} dataKey={`negative${index}`} name={`${item.name} (negative direction)`}
            fill="url(#history-hatch)" isAnimationActive={false} />)
        return bars
      })}
      {markers.map(marker => {
        const x = markerBucket(series[0]?.rows ?? [], marker.at)
        return x ? <ReferenceLine key={marker.key} x={x} stroke="#805108" strokeDasharray="3 3" /> : null
      })}
    </BarChart>
  </ChartBox>
}

/** Hours ON, OFF and without coverage per bucket. The three parts of one bucket's duration may be stacked. */
export function RelayChart({ rows, markers, label }: { rows: BucketRow[]; markers: ReplacementMarker[]; label: string }) {
  return <ChartBox label={label}>
    <BarChart data={rows} margin={{ left: 4, right: 14, top: 12, bottom: 0 }}>
      <Patterns />
      <CartesianGrid vertical={false} stroke="#e5eaf0" strokeDasharray="4 4" />
      <XAxis dataKey="label" minTickGap={24} tickLine={false} axisLine={false} tick={axis} />
      <YAxis tickLine={false} axisLine={false} tick={axis} width={48} tickFormatter={value => `${Number(value).toFixed(0)} h`} />
      <Tooltip content={({ active, payload }) => active && payload?.length
        ? <BucketTooltip entries={[{ name: 'Relay', unit: null, row: payload[0].payload as BucketRow }]} /> : null} />
      <Bar dataKey="onHours" name="ON" stackId="state" fill="#0b705d" isAnimationActive={false} />
      <Bar dataKey="offHours" name="OFF" stackId="state" fill="#c9d3dd" isAnimationActive={false} />
      <Bar dataKey="unknownHours" name="No coverage" stackId="state" fill="url(#history-unknown)" isAnimationActive={false} />
      {markers.map(marker => {
        const x = markerBucket(rows, marker.at)
        return x ? <ReferenceLine key={marker.key} x={x} stroke="#805108" strokeDasharray="3 3" /> : null
      })}
    </BarChart>
  </ChartBox>
}

/** Raw readings on a true time axis. Null rows break the line at gaps and device changes. */
export function RawChart({ rows, unit, aggregation, markers, start, end, timeZone, label }: {
  rows: RawRow[]; unit: string | null; aggregation: MetricAggregation; markers: ReplacementMarker[]; start: number; end: number
  timeZone: string; label: string
}) {
  const boolean = aggregation === 'boolean'
  const tick = (value: number) => new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit', timeZone }).format(value)
  return <ChartBox label={label}>
    <LineChart data={rows} margin={{ left: 4, right: 14, top: 12, bottom: 0 }}>
      <CartesianGrid vertical={false} stroke="#e5eaf0" strokeDasharray="4 4" />
      <XAxis type="number" dataKey="time" domain={[start, end]} scale="time" tickFormatter={tick} minTickGap={32} tickLine={false} axisLine={false} tick={axis} />
      <YAxis tickLine={false} axisLine={false} tick={axis} width={64} domain={boolean ? [0, 1] : ['auto', 'auto']} ticks={boolean ? [0, 1] : undefined}
        tickFormatter={value => boolean ? (Number(value) ? 'ON' : 'OFF') : formatValue(Number(value), unit)} />
      <Tooltip labelFormatter={value => formatInstant(Number(value), timeZone)}
        formatter={value => [boolean ? (Number(value) ? 'ON' : 'OFF') : formatValue(Number(value), unit), 'Reading']} />
      <Line dataKey="value" name="Reading" stroke={seriesStyles[0].color} strokeWidth={2} type={boolean ? 'stepAfter' : 'linear'}
        connectNulls={false} isAnimationActive={false} dot={rows.length <= 60 ? { r: 2.5 } : false} />
      {markers.map(marker => <ReferenceLine key={marker.key} x={marker.at} stroke="#805108" strokeDasharray="3 3"
        label={{ value: 'Replaced', position: 'insideTopRight', fill: '#805108', fontSize: 11 }} />)}
    </LineChart>
  </ChartBox>
}

/** One cell per bucket. Each quality has its own pattern as well as a color, and a text tooltip. */
export function QualityStrip({ rows }: { rows: BucketRow[] }) {
  const counts = rows.reduce<Record<string, number>>((total, row) => ({ ...total, [row.quality]: (total[row.quality] ?? 0) + 1 }), {})
  return <div className="history-quality">
    <div className="history-quality-strip" aria-hidden>
      {rows.map(row => <span key={row.key} className={`quality-cell quality-${row.quality}`}
        title={`${row.title}: ${qualityLabels[row.quality]}${row.quality === 'incomplete' || row.quality === 'complete' ? ` (${formatPercent(row.coverage)})` : ''}`} />)}
    </div>
    <ul className="history-quality-legend" aria-label="Data quality">
      {(Object.keys(qualityLabels) as (keyof typeof qualityLabels)[]).filter(quality => counts[quality]).map(quality =>
        <li key={quality} title={qualityHelp[quality]}><span className={`quality-cell quality-${quality}`} aria-hidden />
          {qualityLabels[quality]}: {counts[quality]}</li>)}
    </ul>
  </div>
}

export function ReplacementList({ markers, timeZone }: { markers: ReplacementMarker[]; timeZone: string }) {
  if (markers.length === 0) return null
  return <ul className="history-replacements" aria-label="Device replacements">
    {markers.map(marker => <li key={marker.key}>Device replaced {formatInstant(marker.at, timeZone)}: {marker.from} → {marker.to}</li>)}
  </ul>
}

/** Visually hidden table with every bucket, so values, gaps and quality are available without the chart. */
export function BucketTable({ caption, series, kind }: {
  caption: string; series: { name: string; unit: string | null; rows: BucketRow[] }[]; kind: 'measurement' | 'energy' | 'relay'
}) {
  const rows = series[0]?.rows ?? []
  return <table className="sr-only">
    <caption>{caption}</caption>
    <thead><tr><th scope="col">Period</th>{series.map(item => <th key={item.name} scope="col">{item.name}</th>)}</tr></thead>
    <tbody>{rows.map((row, index) => <tr key={row.key}>
      <th scope="row">{row.title}</th>
      {series.map(item => {
        const cell = item.rows[index]
        return <td key={item.name}>{cell ? describe(cell, item.unit, kind) : 'No data'}</td>
      })}
    </tr>)}</tbody>
  </table>
}

function describe(row: BucketRow, unit: string | null, kind: 'measurement' | 'energy' | 'relay') {
  const quality = row.quality === 'complete' ? '' : ` · ${qualityLabels[row.quality]}${row.quality === 'incomplete' ? ` (${formatPercent(row.coverage)})` : ''}`
  if (kind === 'energy') {
    const parts = [row.measuredKwh !== null ? `Measured ${formatValue(row.measuredKwh, 'kWh')}${row.measuredPartial ? ' (partial)' : ''}` : null,
      row.estimatedKwh !== null ? `Estimated ${formatValue(row.estimatedKwh, 'kWh')}${row.estimatedPartial ? ' (partial)' : ''}` : null,
      row.estimatedNegativeKwh !== null ? `Negative direction ${formatValue(row.estimatedNegativeKwh, 'kWh')}` : null].filter(Boolean)
    return (parts.length ? parts.join(', ') : 'No energy value') + quality
  }
  if (kind === 'relay') return row.onHours === null ? `No state${quality}` : `ON ${formatHours(row.onHours * 3600)} of ${formatHours(row.bucket.coveredSeconds)}${quality}`
  return row.average === null ? `No value${quality}`
    : `Average ${formatValue(row.average, unit)}, minimum ${formatValue(row.minimum, unit)}, maximum ${formatValue(row.maximum, unit)}${quality}`
}

/** Range statistics from backend buckets, weighted by covered seconds. Incomplete totals are labelled as such. */
export function SummaryStats({ summary, unit, aggregation, power }: { summary: Summary; unit: string | null; aggregation: MetricAggregation; power: boolean }) {
  const coverage = summary.elapsedSeconds > 0 ? formatPercent(summary.coverage) : '—'
  const incomplete = summary.coverage < incompleteBelow || summary.notAggregated
  const items: { label: string; value: string; note?: string }[] = []
  if (aggregation === 'gauge') {
    items.push({ label: 'Average', value: formatValue(summary.average, unit), note: 'time-weighted' })
    items.push({ label: 'Minimum', value: formatValue(summary.minimum, unit) }, { label: 'Maximum', value: formatValue(summary.maximum, unit) })
    if (power) items.push({ label: 'Estimated energy', value: formatValue(summary.estimatedWh === null ? null : summary.estimatedWh / 1000, 'kWh'),
      note: summary.estimatedWh === null ? undefined : summary.estimatedComplete ? 'from power readings' : 'partial, from power readings' })
  } else if (aggregation === 'boolean') {
    items.push({ label: 'ON duration', value: formatHours(summary.onSeconds) }, { label: 'Covered duration', value: formatHours(summary.coveredSeconds) })
  } else {
    items.push({ label: 'Measured energy', value: formatValue(summary.measuredWh === null ? null : summary.measuredWh / 1000, 'kWh'),
      note: summary.measuredWh === null ? undefined : summary.measuredComplete ? 'device counter' : 'partial, device counter' })
  }
  items.push({ label: 'Coverage', value: coverage, note: summary.notAggregated ? 'part of the range is not aggregated yet' : incomplete ? 'incomplete' : undefined })
  return <dl className="history-summary">
    {items.map(item => <div key={item.label}><dt>{item.label}</dt><dd>{item.value}{item.note && <small>{item.note}</small>}</dd></div>)}
  </dl>
}
