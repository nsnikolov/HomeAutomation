import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { ArrowUpRight, ChevronDown, ChevronRight } from 'lucide-react'
import { Link, useNavigate } from 'react-router'
import type { DashboardGroup, DashboardReading, DashboardSource, DashboardValue, DashboardWidget, Property } from '../shared/types'
import { metricLabels } from './Devices'

export const providerNames: Record<DashboardSource['provider'], string> = {
  growatt: 'Growatt', daikin: 'Daikin ONECTA', ewelink: 'eWeLink', tuya: 'Smart Life / Tuya', simulator: 'Simulator (simulated)',
}
const freshnessSteps = [0, 5 * 60000, 60 * 60000]
const timestampWithOffset = /^(\d{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01]))T(?:[01]\d|2[0-3]):[0-5]\d:[0-5]\d(?:\.\d{1,7})?(?:Z|[+-](?:[01]\d|2[0-3]):[0-5]\d)$/
const freshnessLabels = {
  fresh: 'Under 5 minutes old', aging: '5 to under 60 minutes old', old: '60 minutes or older', missing: 'Unavailable',
}
const provenanceLabels = { provider: 'Provider observation', serverReceived: 'Server received', simulated: 'Simulated observation' }
const numeric = new Intl.NumberFormat(undefined, { maximumFractionDigits: 20 })

export type DashboardPresentation = { mode: DashboardWidget['mode']; generation: number }

export function DashboardViewToggle({ label, selected, onSelect, title }: {
  label: string; selected?: DashboardWidget['mode']; onSelect: (mode: DashboardWidget['mode']) => void; title?: string
}) {
  return <div className="monitor-view-toggle" role="group" aria-label={label} title={title}>
    {(['compact', 'detailed'] as const).map(mode => <button key={mode} type="button"
      aria-pressed={selected === mode} onClick={() => onSelect(mode)}>
      {mode === 'compact' ? 'Compact' : 'Detailed'}
    </button>)}
  </div>
}

function observationTime(reading: DashboardReading | null | undefined) {
  if (!reading || typeof reading.observedAt !== 'string') return null
  const validValue = reading.number !== null ? Number.isFinite(reading.number)
    : typeof reading.flag === 'boolean' || (typeof reading.code === 'string' && reading.code.trim() !== '')
  const match = validValue && timestampWithOffset.exec(reading.observedAt)
  if (!match) return null
  const date = new Date(`${match[1]}T00:00:00Z`)
  if (Number.isNaN(date.getTime()) || date.toISOString().slice(0, 10) !== match[1]) return null
  const instant = Date.parse(reading.observedAt)
  return Number.isFinite(instant) ? instant : null
}

function freshnessAt(at: number | null, now: number): keyof typeof freshnessLabels {
  if (at === null) return 'missing'
  const age = now - at
  if (!Number.isFinite(age) || age < 0) return 'missing'
  return age < freshnessSteps[1] ? 'fresh' : age < freshnessSteps[2] ? 'aging' : 'old'
}

export function dashboardFreshness(reading: DashboardReading | null | undefined, now: number) {
  const at = observationTime(reading)
  return { at, tier: freshnessAt(at, now) }
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
        const at = observationTime(source.reading)
        if (at === null) continue
        for (const step of freshnessSteps) if (at + step > current) next = Math.min(next, at + step)
      }
      if (Number.isFinite(next)) timer = setTimeout(tick, Math.min(next - current, 2147483647))
    }
    tick()
    document.addEventListener('visibilitychange', tick)
    window.addEventListener('focus', tick)
    return () => {
      clearTimeout(timer)
      document.removeEventListener('visibilitychange', tick)
      window.removeEventListener('focus', tick)
    }
  }, [sources])
  return now
}

function PreviewGrid({ items, label, kind, initiallyCollapsed = true, presentation, heading }: {
  items: { id: string; content: ReactNode }[]; label: string; kind: 'widgets' | 'values'; initiallyCollapsed?: boolean
  presentation?: DashboardPresentation; heading?: ReactNode
}) {
  const id = useId()
  const grid = useRef<HTMLDivElement>(null)
  const group = kind === 'widgets'
  const previewRows = group ? 1 : 2
  const [limit, setLimit] = useState<{ count: number; height: number } | null>(null)
  const [override, setOverride] = useState<{ expanded: boolean; generation: number | undefined }>()
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
        if (row > previewRows) {
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
  }, [itemKeys, previewRows])

  const localExpanded = override?.generation === presentation?.generation ? override?.expanded : undefined
  const expanded = localExpanded ?? (presentation ? presentation.mode === 'detailed' : !initiallyCollapsed)
  const toggle = () => setOverride({ expanded: !expanded, generation: presentation?.generation })
  const cropped = !expanded && limit !== null
  return <>
    {group && <div className="monitor-group-heading">
      {heading}
      <button type="button" className="monitor-group-toggle" aria-label={`${expanded ? 'Collapse' : 'Expand'} ${label}`}
        aria-controls={id} aria-expanded={expanded} onClick={toggle}>
        {expanded ? <ChevronDown size={17} aria-hidden /> : <ChevronRight size={17} aria-hidden />}
      </button>
    </div>}
    <div id={id} className={cropped ? 'monitor-cropped' : undefined} style={cropped ? { maxHeight: limit.height } : undefined}>
      <div ref={grid} className={`monitor-grid monitor-grid-${kind}`}>
        {items.map((item, index) =>
          <div key={item.id} className="monitor-grid-item" inert={cropped && index >= limit.count}
            aria-hidden={cropped && index >= limit.count || undefined}
            data-overflow={cropped && index >= limit.count ? 'true' : undefined}>{item.content}</div>)}
      </div>
      {group && items.length === 0 && <p className="muted">This group has no widgets.</p>}
    </div>
    {!group && limit && <button type="button" className="monitor-expand" aria-label={`${expanded ? 'Collapse' : 'Show all'} ${label}`}
      aria-controls={id} aria-expanded={expanded} onClick={toggle}>{expanded ? 'Collapse' : 'Show all'}</button>}
  </>
}

export function DashboardGroups({ groups, sources, property, now, presentation }: {
  groups: DashboardGroup[]; sources: Map<string, DashboardSource>; property: Property; now: number
  presentation?: DashboardPresentation
}) {
  return <div className="monitor-groups">
    {groups.filter(group => !group.hidden).map(group => <section key={group.id} className="monitor-group" aria-labelledby={`group-${group.id}`}>
      <PreviewGrid label={group.title} kind="widgets" initiallyCollapsed={group.collapsed} presentation={presentation}
        heading={<h2 id={`group-${group.id}`}>{group.title}</h2>}
        items={group.widgets.map(widget => ({ id: widget.id, content:
          <MonitoringWidget widget={widget} sources={sources} property={property} now={now} presentation={presentation} />,
        }))} />
    </section>)}
  </div>
}

export function MonitoringWidget({ widget, sources, property, now, preview = false, presentation }: {
  widget: DashboardWidget; sources: Map<string, DashboardSource>; property: Property; now: number; preview?: boolean
  presentation?: DashboardPresentation
}) {
  const navigate = useNavigate()
  const [local, setLocal] = useState<{ mode: DashboardWidget['mode']; generation?: number }>()
  const localMode = local?.generation === presentation?.generation ? local?.mode : undefined
  const mode = localMode ?? presentation?.mode ?? widget.mode
  const dateFormat = useMemo(() => new Intl.DateTimeFormat(undefined,
    { dateStyle: 'full', timeStyle: 'long', timeZone: property.timeZone }), [property.timeZone])
  const target = widget.navigationSource && sources.get(widget.navigationSource)
  const base = `/properties/${property.id}`
  const route = !preview && target && (target.provider === 'simulator'
    ? /^\d+$/.test(target.deviceId) && target.route === `${base}/devices/${target.deviceId}`
    : ['growatt', 'daikin', 'ewelink', 'tuya'].includes(target.provider) && target.route === `${base}/${target.provider}`)
    ? target.route : undefined
  return <article className={`monitor-widget monitor-${mode}${route ? ' monitor-linked' : ''}`}
    onClick={route ? event => {
      if (event.target instanceof Element && !event.target.closest('a,button,.monitor-widget-actions') && !window.getSelection()?.toString()) void navigate(route)
    } : undefined}>
    <div className="monitor-widget-header">
      <h3>{widget.title}</h3>
      {!preview && <div className="monitor-widget-actions">
        <DashboardViewToggle label={`${widget.title} display mode`} selected={mode}
          onSelect={mode => setLocal({ mode, generation: presentation?.generation })} />
        {route && <Link className="monitor-widget-link" to={route} aria-label={`Open ${widget.title}`}>
          <ArrowUpRight size={14} aria-hidden />
        </Link>}
      </div>}
    </div>
    <PreviewGrid label={widget.title} kind="values" presentation={presentation} items={widget.values.map(value => ({
      id: value.id, content: <Measurement value={value} source={sources.get(value.sourceKey)} now={now}
        dateFormat={dateFormat} detailed={mode === 'detailed'} />,
    }))} />
  </article>
}

function Measurement({ value, source, now, dateFormat, detailed }: {
  value: DashboardValue; source?: DashboardSource; now: number; dateFormat: Intl.DateTimeFormat; detailed: boolean
}) {
  const reading = source?.reading
  const { at, tier } = dashboardFreshness(reading, now)
  const title = value.label || (source ? metricLabels[source.metric] : 'Measurement')
  const origin = source ? `${providerNames[source.provider]} · ${source.deviceName}` : ''
  let measurement = '—'
  let summary = source ? 'No valid dated reading is available.' : 'The source is no longer available for this property.'
  let stamp: string | undefined
  let originalStamp: string | undefined
  let provenance: string | undefined
  if (reading && at !== null && tier !== 'missing') {
    measurement = reading.number !== null ? numeric.format(reading.number) + (source?.unit === '%' ? '%' : source?.unit ? ` ${source.unit}` : '')
      : reading.flag !== null ? (reading.flag ? 'ON' : 'OFF') : reading.code!
    summary = freshnessLabels[tier]
    stamp = dateFormat.format(at)
    originalStamp = reading.observedAt
    provenance = provenanceLabels[reading.provenance]
  }
  const tooltip = [origin, summary, stamp && `${stamp} (${originalStamp})`, provenance].filter(Boolean).join(' · ')
  return <div className="monitor-measure">
    <span className="monitor-measure-label">{title}{source?.provider === 'simulator' && ' (simulated)'}</span>
    <strong className={`monitor-age-${tier}`} title={tooltip}>{measurement}</strong>
    <span className={detailed ? 'monitor-measure-detail' : 'sr-only'}>{origin && <>{origin}<br /></>}{summary}
      {stamp && <> · <time dateTime={originalStamp}>{stamp}</time> · {provenance}</>}
    </span>
  </div>
}
