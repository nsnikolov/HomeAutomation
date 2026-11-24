import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { ArrowUpRight } from 'lucide-react'
import { Link, useNavigate } from 'react-router'
import type { DashboardGroup, DashboardReading, DashboardSource, DashboardValue, DashboardWidget, Property } from '../shared/types'
import { metricLabels } from './Devices'

export const providerNames: Record<DashboardSource['provider'], string> = {
  growatt: 'Growatt', daikin: 'Daikin ONECTA', ewelink: 'eWeLink', tuya: 'Smart Life / Tuya', simulator: 'Simulator (simulated)',
}
const freshnessSteps = [0, 5 * 60000, 60 * 60000]
const freshnessLabels = {
  fresh: 'Under 5 minutes old', aging: '5 to under 60 minutes old', old: '60 minutes or older', missing: 'Unavailable',
}
const provenanceLabels = { provider: 'Provider observation', serverReceived: 'Server received', simulated: 'Simulated observation' }
const numeric = new Intl.NumberFormat(undefined, { maximumFractionDigits: 20 })

export function dashboardFreshness(reading: DashboardReading | null | undefined, now: number) {
  if (!reading || (reading.number === null ? reading.flag === null && !reading.code : !Number.isFinite(reading.number)))
    return 'missing'
  const age = now - Date.parse(reading.observedAt)
  if (!Number.isFinite(age) || age < 0) return 'missing'
  return age < freshnessSteps[1] ? 'fresh' : age < freshnessSteps[2] ? 'aging' : 'old'
}

export function useDashboardTime(sources: readonly DashboardSource[]) {
  const [now, setNow] = useState(Date.now)
  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined
    function tick() {
      clearTimeout(timer)
      const current = Date.now()
      setNow(current)
      let next = Number.POSITIVE_INFINITY
      for (const source of sources) {
        if (!source.reading) continue
        const at = Date.parse(source.reading.observedAt)
        for (const step of freshnessSteps) if (at + step > current) next = Math.min(next, at + step)
      }
      if (Number.isFinite(next)) timer = setTimeout(tick, Math.min(next - current, 2147483647))
    }
    tick()
    document.addEventListener('visibilitychange', tick)
    return () => { clearTimeout(timer); document.removeEventListener('visibilitychange', tick) }
  }, [sources])
  return now
}

export function TwoRowGrid({ items, label, kind, initiallyCollapsed = true }: {
  items: { id: string; content: ReactNode }[]; label: string; kind: 'widgets' | 'values'; initiallyCollapsed?: boolean
}) {
  const id = useId()
  const grid = useRef<HTMLDivElement>(null)
  const [limit, setLimit] = useState<{ count: number; height: number } | null>(null)
  const [override, setOverride] = useState<boolean>()
  const itemKeys = items.map(item => item.id).join('|')
  useLayoutEffect(() => {
    const element = grid.current!
    function measure() {
      if (!element.getClientRects().length) return
      let row = 0
      let height = 0
      let lastTop = -1
      for (const [index, child] of Array.from(element.children).entries()) {
        const item = child as HTMLElement
        if (item.offsetTop !== lastTop) { row++; lastTop = item.offsetTop }
        if (row > 2) {
          setLimit(previous => previous?.count === index && previous.height === height ? previous : { count: index, height })
          return
        }
        height = Math.max(height, item.offsetTop + item.offsetHeight)
      }
      setLimit(null)
    }
    measure()
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(measure)
    observer?.observe(element)
    Array.from(element.children).forEach(item => observer?.observe(item))
    return () => observer?.disconnect()
  }, [itemKeys])

  const expanded = override ?? !initiallyCollapsed
  const cropped = !expanded && limit !== null
  return <>
    <div id={id} className={cropped ? 'monitor-cropped' : undefined} style={cropped ? { maxHeight: limit.height } : undefined}>
      <div ref={grid} className={`monitor-grid monitor-grid-${kind}`}>
        {items.map((item, index) =>
          <div key={item.id} className="monitor-grid-item" inert={cropped && index >= limit.count}
            aria-hidden={cropped && index >= limit.count || undefined}
            data-overflow={cropped && index >= limit.count ? 'true' : undefined}>{item.content}</div>)}
      </div>
    </div>
    {limit && <button type="button" className="monitor-expand" aria-label={`${expanded ? 'Collapse' : 'Show all'} ${label}`}
      aria-controls={id} aria-expanded={expanded} onClick={() => setOverride(!expanded)}>{expanded ? 'Collapse' : 'Show all'}</button>}
  </>
}

export function DashboardGroups({ groups, sources, property, now }: {
  groups: DashboardGroup[]; sources: Map<string, DashboardSource>; property: Property; now: number
}) {
  return <div className="monitor-groups">
    {groups.filter(group => !group.hidden).map(group => <section key={group.id} className="monitor-group" aria-labelledby={`group-${group.id}`}>
      <h2 id={`group-${group.id}`}>{group.title}</h2>
      {group.widgets.length === 0 ? <p className="muted">This group has no widgets.</p> :
        <TwoRowGrid label={group.title} kind="widgets" initiallyCollapsed={group.collapsed}
          items={group.widgets.map(widget => ({ id: widget.id, content:
            <MonitoringWidget widget={widget} sources={sources} property={property} now={now} />,
          }))} />}
    </section>)}
  </div>
}

export function MonitoringWidget({ widget, sources, property, now, preview = false }: {
  widget: DashboardWidget; sources: Map<string, DashboardSource>; property: Property; now: number; preview?: boolean
}) {
  const navigate = useNavigate()
  const dateFormat = useMemo(() => new Intl.DateTimeFormat(undefined,
    { dateStyle: 'full', timeStyle: 'long', timeZone: property.timeZone }), [property.timeZone])
  const target = widget.navigationSource && sources.get(widget.navigationSource)
  const base = `/properties/${property.id}`
  const route = !preview && target && (target.provider === 'simulator'
    ? /^\d+$/.test(target.deviceId) && target.route === `${base}/devices/${target.deviceId}`
    : ['growatt', 'daikin', 'ewelink', 'tuya'].includes(target.provider) && target.route === `${base}/${target.provider}`)
    ? target.route : undefined
  return <article className={`monitor-widget monitor-${widget.mode}${route ? ' monitor-linked' : ''}`}
    onClick={route ? event => {
      if (event.target instanceof Element && !event.target.closest('a,button') && !window.getSelection()?.toString()) void navigate(route)
    } : undefined}>
    <h3>{route ? <Link to={route}>{widget.title}<ArrowUpRight size={14} aria-hidden /></Link> : widget.title}</h3>
    <TwoRowGrid label={widget.title} kind="values" items={widget.values.map(value => ({
      id: value.id, content: <Measurement value={value} source={sources.get(value.sourceKey)} now={now}
        dateFormat={dateFormat} detailed={widget.mode === 'detailed'} />,
    }))} />
  </article>
}

function Measurement({ value, source, now, dateFormat, detailed }: {
  value: DashboardValue; source?: DashboardSource; now: number; dateFormat: Intl.DateTimeFormat; detailed: boolean
}) {
  const reading = source?.reading
  const freshness = dashboardFreshness(reading, now)
  const title = value.label || (source ? metricLabels[source.metric] : 'Measurement')
  const origin = source ? `${providerNames[source.provider]} · ${source.deviceName}` : ''
  let measurement = '—'
  let details = source ? 'No valid dated reading is available.' : 'The source is no longer available for this property.'
  if (reading && freshness !== 'missing') {
    measurement = reading.number !== null ? numeric.format(reading.number) + (source?.unit === '%' ? '%' : source?.unit ? ` ${source.unit}` : '')
      : reading.flag !== null ? (reading.flag ? 'ON' : 'OFF') : reading.code!
    const stamp = dateFormat.format(new Date(reading.observedAt))
    details = `${freshnessLabels[freshness]} · ${stamp} · ${provenanceLabels[reading.provenance]}`
  }
  return <div className="monitor-measure">
    <span className="monitor-measure-label">{title}{source?.provider === 'simulator' && ' (simulated)'}</span>
    <strong className={`monitor-age-${freshness}`} title={`${origin ? origin + ' · ' : ''}${details}`}>{measurement}</strong>
    <span className={detailed ? 'monitor-measure-detail' : 'sr-only'}>{origin && <>{origin}<br /></>}{details}</span>
  </div>
}
