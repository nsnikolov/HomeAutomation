import { useMemo, useState } from 'react'
import { useQueries, useQuery } from '@tanstack/react-query'
import { RefreshCw } from 'lucide-react'
import { useSearchParams } from 'react-router'
import { api, LogicalApiError, type LogicalHistoryRequest } from '../../shared/api'
import type { LogicalDevice, LogicalHistory, LogicalMetric, Metric, Property } from '../../shared/types'
import { Button, Card, Input } from '../../shared/ui'
import { metricLabels } from '../Devices'
import {
  BucketTable, EnergyChart, MeasurementChart, QualityStrip, RawChart, RelayChart, ReplacementList, SummaryStats, type ComparisonSeries,
} from './HistoryCharts'
import {
  bucketRows, formatInstant, historyWindow, isEnergyMetric, localInput, monthly, rawPointLimit, rawRows, replacementMarkers, summarize,
  type HistoryWindow, type RangePreset, type ViewResolution,
} from './historyModel'

const presets: { value: RangePreset; label: string }[] = [
  { value: '24h', label: 'Last 24 hours' }, { value: '7d', label: 'Last 7 days' }, { value: '30d', label: 'Last 30 days' },
  { value: '12m', label: 'Last 12 months' }, { value: 'custom', label: 'Custom range' },
]
const resolutionLabels: Record<ViewResolution, string> = { raw: 'Individual readings', hour: 'Hourly', day: 'Daily', month: 'Monthly' }
const preferredMetrics: Metric[] = ['temperature', 'humidity', 'activePower', 'energy', 'switchOn']
const maximumComparisons = 3
type Choice = { device: LogicalDevice; metric: LogicalMetric }

/** Metrics with reliable historical semantics. Status codes are raw-only and are not charted here. */
const charted = (device: LogicalDevice) => (device.metrics ?? []).filter(metric => metric.aggregation !== 'status')

export function HistoryPage({ property }: { property: Property }) {
  const [params, setParams] = useSearchParams()
  const devices = useQuery({
    queryKey: ['property', property.id, 'logical-devices'], queryFn: ({ signal }) => api.logicalDevices(property.id, signal), retry: false,
  })
  const [anchor, setAnchor] = useState(() => Date.now())
  const update = (changes: Record<string, string | null>) => setParams(current => {
    const next = new URLSearchParams(current)
    for (const [key, value] of Object.entries(changes)) if (value === null || value === '') next.delete(key); else next.set(key, value)
    return next
  }, { replace: true })

  const heading = <div className="page-heading"><h1>History</h1>
    <p>Recorded measurements of {property.name}, by logical device. Times use the property time zone ({property.timeZone}).</p></div>
  if (devices.isPending) return <>{heading}<p role="status">Loading logical devices…</p></>
  if (devices.isError) return <>{heading}<Unavailable error={devices.error} /></>

  const available = devices.data.devices.filter(device => charted(device).length > 0)
  if (available.length === 0) return <>{heading}<Card className="placeholder"><p className="muted">
    {devices.data.devices.length === 0 ? 'No logical devices exist yet. An administrator can create them for the existing devices with the documented initialization command.'
      : 'No logical device has recorded measurements yet.'}</p></Card></>

  const device = available.find(item => String(item.id) === params.get('device')) ?? available[0]
  const metrics = charted(device)
  const metric = metrics.find(item => item.metric === params.get('metric') && item.channelKey === (params.get('channel') ?? item.channelKey))
    ?? preferredMetrics.map(name => metrics.find(item => item.metric === name)).find(Boolean) ?? metrics[0]
  const preset = presets.some(item => item.value === params.get('range')) ? params.get('range') as RangePreset : '24h'
  const override = (['raw', 'hour', 'day', 'month'] as const).find(value => value === params.get('res')) ?? null
  const custom = { from: params.get('from') ?? localInput(anchor - 86_400_000, property.timeZone), to: params.get('to') ?? localInput(anchor, property.timeZone) }
  const window = historyWindow(preset, anchor, property.timeZone, override, custom)
  const comparable = available.flatMap(item => item.id === device.id ? [] : charted(item)
    .filter(entry => entry.metric === metric.metric).map(entry => ({ device: item, metric: entry })))
  const compared = (params.get('compare') ?? '').split(',').map(value => comparable.find(item => `${item.device.id}:${item.metric.channelKey}` === value))
    .filter((item): item is Choice => item !== undefined).slice(0, maximumComparisons)

  return <>
    {heading}
    <Card className="history-filters">
      <label>Logical device<select className="input select-input" value={device.id}
        onChange={event => update({ device: event.target.value, metric: null, channel: null, compare: null })}>
        {available.map(item => <option key={item.id} value={item.id}>{item.name}{item.archivedAt ? ' (archived)' : ''}</option>)}
      </select></label>
      <label>Measurement<select className="input select-input" value={`${metric.channelKey}|${metric.metric}`} onChange={event => {
        const [channel, value] = event.target.value.split('|')
        update({ channel, metric: value, compare: null })
      }}>
        {metrics.map(item => <option key={`${item.channelKey}|${item.metric}`} value={`${item.channelKey}|${item.metric}`}>
          {metricLabels[item.metric]}{item.unit ? ` (${item.unit})` : ''}{metrics.some(other => other.channelKey !== item.channelKey) ? ` · ${item.channelKey}` : ''}
          {item.current ? '' : ' — not reported by the current device'}
        </option>)}
      </select></label>
      <label>Time range<select className="input select-input" value={preset} onChange={event => { setAnchor(Date.now()); update({ range: event.target.value, res: null }) }}>
        {presets.map(item => <option key={item.value} value={item.value}>{item.label}</option>)}
      </select></label>
      {'allowed' in window && window.allowed.length > 1 && <label>Resolution<select className="input select-input" value={window.resolution}
        onChange={event => update({ res: event.target.value })}>
        {window.allowed.map(value => <option key={value} value={value}>{resolutionLabels[value]}</option>)}
      </select></label>}
      <Button variant="outline" type="button" onClick={() => setAnchor(Date.now())} aria-label="Refresh history"><RefreshCw size={16} /> Refresh</Button>
      {preset === 'custom' && <CustomRange property={property} value={custom} onApply={value => { setAnchor(Date.now()); update({ ...value, res: null }) }} />}
    </Card>
    {'error' in window ? <p role="alert">{window.error}</p> : <>
      <HistoryView property={property} choice={{ device, metric }} window={window} compared={compared} />
      {window.resolution !== 'raw' && comparable.length > 0 && (metric.aggregation === 'gauge' || isEnergyMetric(metric.aggregation)) &&
        <ComparePicker choices={comparable} selected={compared} onChange={value => update({ compare: value.map(item => `${item.device.id}:${item.metric.channelKey}`).join(',') || null })} />}
    </>}
  </>
}

function CustomRange({ property, value, onApply }: { property: Property; value: { from: string; to: string }; onApply: (value: { from: string; to: string }) => void }) {
  const [draft, setDraft] = useState(value)
  return <form className="history-custom" aria-label="Custom range" onSubmit={event => { event.preventDefault(); onApply(draft) }}>
    <label>From<Input type="datetime-local" value={draft.from} onChange={event => setDraft({ ...draft, from: event.target.value })} /></label>
    <label>To<Input type="datetime-local" value={draft.to} onChange={event => setDraft({ ...draft, to: event.target.value })} /></label>
    <Button type="submit" variant="outline">Apply range</Button>
    <p className="muted">Dates and times are in {property.timeZone}. Hourly views cover up to 31 days and daily views up to 366 days.</p>
  </form>
}

function ComparePicker({ choices, selected, onChange }: { choices: Choice[]; selected: Choice[]; onChange: (value: Choice[]) => void }) {
  const key = (item: Choice) => `${item.device.id}:${item.metric.channelKey}`
  return <Card className="history-compare">
    <fieldset>
      <legend>Compare with (up to {maximumComparisons})</legend>
      <p className="muted">Each device keeps its own gaps and coverage. Values are shown side by side and never added together.</p>
      {choices.map(item => {
        const checked = selected.some(entry => key(entry) === key(item))
        return <label key={key(item)}><input type="checkbox" checked={checked} disabled={!checked && selected.length >= maximumComparisons}
          onChange={event => onChange(event.target.checked ? [...selected, item] : selected.filter(entry => key(entry) !== key(item)))} />
          {' '}{item.device.name}</label>
      })}
    </fieldset>
  </Card>
}

function request(choice: Choice, window: HistoryWindow, resolution?: 'hour'): LogicalHistoryRequest {
  const base = { channel: choice.metric.channelKey, metric: choice.metric.metric }
  return resolution ? { ...base, resolution, from: new Date(window.start).toISOString(), to: new Date(window.end).toISOString() }
    : { ...base, ...window.request }
}

function useHistory(property: Property, choice: Choice, window: HistoryWindow, enabled = true, resolution?: 'hour') {
  const body = request(choice, window, resolution)
  return useQuery({
    queryKey: ['property', property.id, 'logical-history', choice.device.id, body],
    queryFn: ({ signal }) => api.logicalHistory(property.id, choice.device.id, body, signal),
    enabled, retry: false, staleTime: 60_000,
  })
}

function HistoryView({ property, choice, window, compared }: { property: Property; choice: Choice; window: HistoryWindow; compared: Choice[] }) {
  const primary = useHistory(property, choice, window)
  const summarySource = useHistory(property, choice, window, window.resolution === 'raw', 'hour')
  const comparisons = useQueries({ queries: compared.map(item => {
    const body = request(item, window)
    return {
      queryKey: ['property', property.id, 'logical-history', item.device.id, body],
      queryFn: ({ signal }: { signal: AbortSignal }) => api.logicalHistory(property.id, item.device.id, body, signal), retry: false, staleTime: 60_000,
    }
  }) })
  const now = window.end
  const { metric, device } = choice
  const title = `${device.name} · ${metricLabels[metric.metric]}`

  if (primary.isPending) return <Card className="history-card"><h2>{title}</h2><p role="status">Loading history…</p></Card>
  if (primary.isError) return <Card className="history-card"><h2>{title}</h2><Unavailable error={primary.error} /></Card>
  const history = primary.data
  const buckets = history.buckets ? window.resolution === 'month' ? monthly(history.buckets, now) : history.buckets : null
  const rows = buckets ? bucketRows(buckets, window.resolution, property.timeZone, now) : []
  const markers = replacementMarkers(history.segments, window.start, window.end)
  const summaryBuckets = window.resolution === 'raw' ? summarySource.data?.buckets ?? null : history.buckets
  const summary = summaryBuckets ? summarize(summaryBuckets, now) : null
  const unsupported = history.segments.filter(segment => segment.status !== 'available' &&
    Date.parse(segment.from) < window.end && (segment.to === null || Date.parse(segment.to) > window.start))
  const series: ComparisonSeries[] = [{ key: 'primary', name: device.name, unit: history.unit, rows },
    ...compared.flatMap((item, index) => {
      const data = comparisons[index]?.data
      if (!data?.buckets) return []
      const compareBuckets = window.resolution === 'month' ? monthly(data.buckets, now) : data.buckets
      return [{ key: `${item.device.id}:${item.metric.channelKey}`, name: item.device.name, unit: data.unit,
        rows: bucketRows(compareBuckets, window.resolution, property.timeZone, now) }]
    })]
  const power = history.unit === 'W' && metric.aggregation === 'gauge'
  const energy = isEnergyMetric(metric.aggregation)
  const empty = rows.length > 0 && rows.every(row => row.quality === 'noData' || row.quality === 'notReported')
  const unaggregated = rows.length > 0 && rows.every(row => row.quality === 'notAggregated')

  return <Card className="history-card">
    <div className="card-heading"><div><h2>{title}</h2>
      <p>{resolutionLabels[window.resolution]} · {formatInstant(window.start, property.timeZone)} to {formatInstant(window.end, property.timeZone)}
        {history.unit ? ` · ${history.unit}` : ''}</p></div></div>
    <Sources history={history} timeZone={property.timeZone} />
    {unsupported.map(segment => <p key={segment.assignmentId} className="notice" role="note">
      {segment.deviceName} {segment.status === 'ambiguous' ? 'reports this measurement on several endpoints' : 'does not report this measurement'}
      {' '}from {formatInstant(Date.parse(segment.from), property.timeZone)}{segment.to ? ` until ${formatInstant(Date.parse(segment.to), property.timeZone)}` : ''}.
      Earlier readings stay available.</p>)}
    {comparisons.some(query => query.isError) && <p role="alert" className="muted">Some compared devices could not be loaded.</p>}
    {window.resolution === 'raw' ? <RawSection history={history} window={window} property={property} markers={markers} title={title} />
      : unaggregated ? <p className="chart-empty" role="status">History for this range has not been aggregated yet.</p>
        : empty ? <p className="chart-empty" role="status">No readings were recorded in this range.</p>
          : <>
            {metric.aggregation === 'boolean'
              ? <RelayChart rows={rows} markers={markers} label={`${title}, ON, OFF and uncovered hours`} />
              : energy ? <EnergyChart label={`${title}, interval energy`} markers={markers}
                series={series.map(item => ({ key: item.key, name: item.name, rows: item.rows, basis: 'measured' }))} />
                : <MeasurementChart series={series} markers={markers} label={`${title}, average with minimum and maximum`} />}
            <Legend kind={metric.aggregation === 'boolean' ? 'relay' : energy ? 'measured' : 'measurement'} series={series} />
            {power && <section aria-label="Estimated energy">
              <h3>Estimated energy</h3>
              <p className="muted">Integrated from power readings over covered time. It is an estimate, not a meter reading, and is never combined
                with measured energy. Hatched bars are estimates; translucent bars with a dashed outline are partial.</p>
              <EnergyChart label={`${title}, estimated energy`} markers={markers}
                series={series.map(item => ({ key: item.key, name: item.name, rows: item.rows, basis: 'estimated' }))} />
              <BucketTable caption={`${title} estimated energy`} kind="energy" series={series} />
            </section>}
            <QualityStrip rows={rows} />
            <BucketTable caption={`${title} by period`} kind={metric.aggregation === 'boolean' ? 'relay' : energy ? 'energy' : 'measurement'} series={series} />
          </>}
    <ReplacementList markers={markers} timeZone={property.timeZone} />
    {summary ? <SummaryStats summary={summary} unit={history.unit} aggregation={metric.aggregation} power={power} />
      : window.resolution === 'raw' && summarySource.isPending ? <p role="status" className="muted">Calculating summary…</p> : null}
    {compared.length > 0 && <ul className="history-compare-summary" aria-label="Comparison coverage">
      {series.slice(1).map(item => {
        const value = summarize(item.rows.map(row => row.bucket), now)
        return <li key={item.key}>{item.name}: average {value.average === null ? '—' : new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(value.average)}
          {item.unit ? ` ${item.unit}` : ''}, coverage {new Intl.NumberFormat(undefined, { style: 'percent' }).format(value.coverage)}</li>
      })}
    </ul>}
  </Card>
}

function RawSection({ history, window, property, markers, title }: {
  history: LogicalHistory; window: HistoryWindow; property: Property; markers: ReturnType<typeof replacementMarkers>; title: string
}) {
  const rows = useMemo(() => rawRows(history.points ?? [], history.segments, history.aggregation), [history])
  if (rows.length === 0) return <p className="chart-empty" role="status">No readings were recorded in this range.</p>
  return <>
    <RawChart rows={rows} unit={history.unit} aggregation={history.aggregation} markers={markers} start={window.start} end={window.end}
      timeZone={property.timeZone} label={`${title}, individual readings`} />
    {history.nextAfter && <p className="muted" role="note">Showing the first {rawPointLimit.toLocaleString()} readings. Choose hourly resolution to see the whole range.</p>}
    <p className="muted">Lines break where readings are further apart than the source's reporting interval and at device replacements.</p>
  </>
}

function Sources({ history, timeZone }: { history: LogicalHistory; timeZone: string }) {
  const shown = history.segments.filter(segment => Date.parse(segment.from) < Date.parse(history.to) && (segment.to === null || Date.parse(segment.to) > Date.parse(history.from)))
  if (shown.length === 0) return null
  return <details className="history-sources">
    <summary>{shown.length === 1 ? `Source: ${shown[0].deviceName}` : `Sources: ${shown.length} physical devices`}</summary>
    <ul>{shown.map(segment => <li key={segment.assignmentId}>{segment.deviceName}
      {segment.source ? ` · ${segment.source}` : ''} · {segment.from.startsWith('1970-01-01') ? 'since its first reading' : `from ${formatInstant(Date.parse(segment.from), timeZone)}`}
      {segment.to ? ` until ${formatInstant(Date.parse(segment.to), timeZone)}` : ''}</li>)}</ul>
  </details>
}

function Legend({ kind, series }: { kind: 'measurement' | 'measured' | 'relay'; series: ComparisonSeries[] }) {
  return <ul className="chart-legend history-legend">
    {kind === 'relay' ? <>
      <li><span className="legend-swatch legend-on" />ON</li><li><span className="legend-swatch legend-off" />OFF</li>
      <li><span className="legend-swatch legend-unknown" />No coverage</li>
    </> : series.map((item, index) => <li key={item.key}><span className={`legend-line legend-line-${index}`} />{item.name}
      {kind === 'measured' ? ' — measured by the device counter' : series.length === 1 ? ' — average' : ''}</li>)}
    {kind === 'measurement' && series.length === 1 && <li><span className="legend-swatch legend-band" />Minimum to maximum</li>}
  </ul>
}

function Unavailable({ error }: { error: Error }) {
  if (error instanceof LogicalApiError && error.status === 503)
    return <Card className="placeholder"><p><strong>History is not available yet.</strong></p><p className="muted">The server has not been upgraded
      for aggregated and logical-device history (migrations 017 and 018). Existing pages keep working.</p></Card>
  if (error instanceof LogicalApiError && error.status === 409) return <p role="alert">{error.message}</p>
  if (error instanceof LogicalApiError && error.status === 400) return <p role="alert">{error.message}</p>
  return <p role="alert">History could not be loaded. Try again later.</p>
}
