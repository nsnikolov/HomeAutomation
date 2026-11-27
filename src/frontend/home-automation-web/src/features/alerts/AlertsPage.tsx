import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useSearchParams } from 'react-router'
import {
  AlertApiError, alertsApi, evidenceValue, formatDuration, qualityLabel, severityLabel, type IncidentEvent, type IncidentFilters,
  type NotificationFilters, type NotificationItem, type NotificationPreferences, type Severity,
} from '../../shared/alerts'
import type { Property } from '../../shared/types'
import { Badge, Button, Card, Input } from '../../shared/ui'
import { formatMoment, humanize } from '../../shared/utils'
import { SeverityBadge, TruthBadge } from './AlertBits'
import { notificationLink, summaryKey, useNotificationReads } from './NotificationCenter'

type Tab = 'incidents' | 'history' | 'settings'

export function AlertsPage({ property, properties }: { property: Property; properties: Property[] }) {
  const [params, setParams] = useSearchParams()
  const incident = Number(params.get('incident')) || null
  const notification = Number(params.get('notification'))
  const [tab, setTab] = useState<Tab>('incidents')
  const reads = useNotificationReads()
  const { markOne } = reads
  // A link from a notification opens its incident.
  useEffect(() => { if (incident) setTab('incidents') }, [incident])
  // A link that names its notification (a system notification click) marks exactly that one read, once. Opening an incident by
  // itself reads nothing: it may have several notifications. Reading never acknowledges or resolves the incident.
  useEffect(() => {
    if (!Number.isSafeInteger(notification) || notification <= 0) return
    void markOne(notification)
    setParams(current => { const next = new URLSearchParams(current); next.delete('notification'); return next }, { replace: true })
  }, [notification, markOne, setParams])
  return <section aria-label="Alerts">
    {reads.error && <p className="form-error" role="alert">{reads.error}</p>}
    <div className="page-heading"><h1>Alerts</h1>
      <p>Incidents, notification history and how you are notified. Alerts never control equipment.</p></div>
    <div className="filters" role="tablist" aria-label="Alert views">
      {([['incidents', 'Incidents'], ['history', 'Notification history'], ['settings', 'Notification settings']] as const).map(([value, label]) =>
        <Button key={value} role="tab" aria-selected={tab === value} variant={tab === value ? 'default' : 'outline'} onClick={() => setTab(value)}>{label}</Button>)}
    </div>
    {tab === 'incidents' && <Incidents property={property} selected={incident}
      select={id => setParams(id ? { incident: String(id) } : {}, { replace: true })} />}
    {tab === 'history' && <History property={property} properties={properties} />}
    {tab === 'settings' && <Settings />}
  </section>
}

function Incidents({ property, selected, select }: { property: Property; selected: number | null; select: (id: number | null) => void }) {
  const [filters, setFilters] = useState<IncidentFilters>({ status: 'active' })
  const list = useQuery({
    queryKey: ['property', property.id, 'alerts', 'incidents', filters],
    queryFn: ({ signal }) => alertsApi.incidents(property.id, filters, signal), retry: false, refetchInterval: 60_000,
  })
  if (list.isError) return <Card className="placeholder"><p className="muted">{list.error instanceof AlertApiError ? list.error.message
    : 'Incidents could not be loaded.'}</p></Card>
  return <div className="incidents">
    <div className="filters">
      <label>Status<select className="input select-input" value={filters.status ?? ''}
        onChange={event => setFilters({ ...filters, status: event.target.value || undefined })}>
        <option value="active">Active</option><option value="resolved">Resolved</option><option value="">All</option></select></label>
      <label>Highest severity<select className="input select-input" value={filters.severity ?? ''}
        onChange={event => setFilters({ ...filters, severity: event.target.value || undefined })}>
        <option value="">All</option><option value="info">Info</option><option value="warning">Warning</option><option value="critical">Critical</option>
      </select></label>
      <label>From<Input type="date" value={filters.from?.slice(0, 10) ?? ''}
        onChange={event => setFilters({ ...filters, from: event.target.value ? new Date(`${event.target.value}T00:00:00`).toISOString() : undefined })} /></label>
      <label>To<Input type="date" value={filters.to?.slice(0, 10) ?? ''}
        onChange={event => setFilters({ ...filters, to: event.target.value ? new Date(`${event.target.value}T23:59:59`).toISOString() : undefined })} /></label>
    </div>
    {list.isPending && <p role="status">Loading incidents…</p>}
    {list.data?.incidents.length === 0 && <Card className="placeholder"><p className="muted">{filters.status === 'active'
      ? 'No active incidents.' : 'No incidents match these filters.'}</p></Card>}
    <ul className="incident-list">{list.data?.incidents.map(incident => <li key={incident.id}>
      <button type="button" className={`incident-row${selected === incident.id ? ' selected' : ''}`} aria-expanded={selected === incident.id}
        onClick={() => select(selected === incident.id ? null : incident.id)}>
        <SeverityBadge severity={incident.severity} />
        <strong>{incident.title}</strong>
        <span className="muted">{incident.status === 'active' ? 'Active' : 'Resolved'} · since {formatMoment(incident.startedAt, property.timeZone)}
          {incident.maxSeverity !== incident.severity ? ` · reached ${severityLabel[incident.maxSeverity]}` : ''}
          {incident.status === 'active' ? incident.acknowledgedAt ? ' · Acknowledged' : ' · Not acknowledged' : ''}</span>
      </button>
      {selected === incident.id && <IncidentPanel property={property} id={incident.id} />}
    </li>)}</ul>
    {selected !== null && !list.data?.incidents.some(incident => incident.id === selected) && list.isSuccess &&
      <IncidentPanel property={property} id={selected} />}
  </div>
}

function IncidentPanel({ property, id }: { property: Property; id: number }) {
  const client = useQueryClient()
  const detail = useQuery({
    queryKey: ['property', property.id, 'alerts', 'incident', id], queryFn: ({ signal }) => alertsApi.incident(property.id, id, signal), retry: false,
  })
  const acknowledge = useMutation({
    mutationFn: () => alertsApi.acknowledge(property.id, id),
    onSuccess: () => Promise.all([client.invalidateQueries({ queryKey: ['property', property.id, 'alerts'] }),
      client.invalidateQueries({ queryKey: summaryKey })]),
  })
  if (detail.isPending) return <p role="status">Loading incident…</p>
  if (detail.isError) return <p className="form-error" role="alert">This incident could not be loaded.</p>
  const { incident, timeline, canAcknowledge } = detail.data
  return <Card className="incident-detail">
    <h2>{incident.title}</h2>
    <p><SeverityBadge severity={incident.severity} /> {incident.status === 'active' ? 'Active' : `Resolved ${incident.resolvedAt
      ? formatMoment(incident.resolvedAt, property.timeZone) : ''}${incident.resolution && incident.resolution !== 'recovered'
      ? ` (${humanize(incident.resolution)})` : ''}`} · started as {severityLabel[incident.originalSeverity]} · highest {severityLabel[incident.maxSeverity]}</p>
    {incident.status === 'active' && <div className="filters">
      {incident.acknowledgedAt ? <span>Acknowledged {formatMoment(incident.acknowledgedAt, property.timeZone)}. Detection continues.</span>
        : canAcknowledge ? <Button onClick={() => acknowledge.mutate()} disabled={acknowledge.isPending}>Acknowledge</Button>
          : <span className="muted">Owners, Admins and Operators can acknowledge incidents.</span>}
      {acknowledge.isError && <p className="form-error" role="alert">{acknowledge.error instanceof AlertApiError ? acknowledge.error.message
        : 'The incident could not be acknowledged.'}</p>}
    </div>}
    <h3>Contributing rules</h3>
    <ul className="audit-list">{incident.rules.map(rule => <li key={rule.ruleId}>
      <SeverityBadge severity={rule.severity} /> {rule.ruleName} <small className="muted">revision {rule.revision} · {rule.active ? 'active'
        : `cleared ${rule.clearedAt ? formatMoment(rule.clearedAt, property.timeZone) : ''}${rule.clearedReason ? ` (${humanize(rule.clearedReason)})` : ''}`}</small>
    </li>)}</ul>
    <h3>Timeline</h3>
    <ol className="incident-timeline">{timeline.map(event => <TimelineEntry key={event.id} event={event} property={property}
      rules={new Map(incident.rules.map(rule => [rule.ruleId, rule.ruleName]))} />)}</ol>
  </Card>
}

const eventText = (event: IncidentEvent, rule: string) => {
  switch (event.kind) {
    case 'opened': return `${severityLabel[event.toSeverity ?? 'warning']} incident opened by ${rule}`
    case 'escalated': return `Escalated from ${severityLabel[event.fromSeverity!]} to ${severityLabel[event.toSeverity!]} by ${rule}`
    case 'deescalated': return `De-escalated from ${severityLabel[event.fromSeverity!]} to ${severityLabel[event.toSeverity!]}`
    case 'ruleActivated': return `${rule} became active`
    case 'ruleCleared': return `${rule} cleared${event.detail?.reason && event.detail.reason !== 'recovered' ? ` (${humanize(event.detail.reason)})` : ''}`
    case 'resolved': return `Resolved${event.detail?.durationSeconds != null ? ` after ${formatDuration(Math.round(event.detail.durationSeconds))}` : ''}${
      event.detail?.reason && event.detail.reason !== 'recovered' ? ` (${humanize(event.detail.reason)})` : ''}`
    case 'acknowledged': return 'Acknowledged'
    case 'reminder': return 'Reminder sent'
    default: return humanize(event.kind)
  }
}

function TimelineEntry({ event, property, rules }: { event: IncidentEvent; property: Property; rules: Map<number, string> }) {
  const rule = event.detail?.ruleName ?? (event.ruleId ? rules.get(event.ruleId) : undefined) ?? 'a rule'
  const evidence = event.detail?.evidence?.flatMap(condition => condition.signals) ?? []
  return <li>
    <time dateTime={event.occurredAt}>{formatMoment(event.occurredAt, property.timeZone)}</time> — {eventText(event, rule)}
    {event.toSeverity && <> <SeverityBadge severity={event.toSeverity} /></>}
    {evidence.length > 0 && <ul className="evidence">{evidence.map((signal, index) => <li key={index}>
      {signal.label}: {evidenceValue(signal)}{signal.observedAt && <> observed <time dateTime={signal.observedAt}>
        {formatMoment(signal.observedAt, property.timeZone)}</time></>} · {qualityLabel[signal.quality]} · <TruthBadge value={signal.result} />
    </li>)}</ul>}
  </li>
}

const kindLabel: Record<string, string> = {
  activated: 'Opened', escalated: 'Escalated', deescalated: 'De-escalated', resolved: 'Recovered', reminder: 'Reminder',
}

function History({ property, properties }: { property: Property; properties: Property[] }) {
  const reads = useNotificationReads()
  const [filters, setFilters] = useState<NotificationFilters>({ propertyId: property.id })
  const [pages, setPages] = useState<number[]>([])
  useEffect(() => setPages([]), [filters])
  const first = useQuery({
    queryKey: ['notifications', 'list', filters], queryFn: ({ signal }) => alertsApi.notifications(filters, signal), retry: false,
  })
  const more = useQuery({
    queryKey: ['notifications', 'list', filters, 'more', pages],
    queryFn: async ({ signal }) => (await Promise.all(pages.map(before => alertsApi.notifications({ ...filters, before }, signal)))),
    enabled: pages.length > 0, retry: false,
  })
  const items: NotificationItem[] = [...first.data?.items ?? [], ...(more.data ?? []).flatMap(page => page.items)]
  const next = pages.length > 0 ? more.data?.at(-1)?.next : first.data?.next
  const set = (patch: Partial<NotificationFilters>) => setFilters(current => ({ ...current, ...patch }))
  return <div className="notification-history">
    <div className="filters">
      <label>Property<select className="input select-input" value={filters.propertyId ?? ''}
        onChange={event => set({ propertyId: event.target.value ? Number(event.target.value) : undefined })}>
        <option value="">All properties</option>{properties.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
      <label>Severity<select className="input select-input" value={filters.severity ?? ''} onChange={event => set({ severity: event.target.value || undefined })}>
        <option value="">All</option><option value="info">Info</option><option value="warning">Warning</option><option value="critical">Critical</option>
      </select></label>
      <label>Event<select className="input select-input" value={filters.kind ?? ''} onChange={event => set({ kind: event.target.value || undefined })}>
        <option value="">All</option>{Object.entries(kindLabel).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
      <label>Incident<select className="input select-input" value={filters.incidentStatus ?? ''}
        onChange={event => set({ incidentStatus: event.target.value || undefined })}>
        <option value="">Active or resolved</option><option value="active">Active</option><option value="resolved">Resolved</option></select></label>
      <label>Read<select className="input select-input" value={filters.unread === undefined ? '' : filters.unread ? 'unread' : 'read'}
        onChange={event => set({ unread: event.target.value === '' ? undefined : event.target.value === 'unread' })}>
        <option value="">All</option><option value="unread">Unread</option><option value="read">Read</option></select></label>
      <label>From<Input type="date" onChange={event => set({ from: event.target.value ? new Date(`${event.target.value}T00:00:00`).toISOString() : undefined })} /></label>
      <label>To<Input type="date" onChange={event => set({ to: event.target.value ? new Date(`${event.target.value}T23:59:59`).toISOString() : undefined })} /></label>
      <Button variant="outline" disabled={reads.markingAll} onClick={() => reads.markAll(filters.propertyId)}>Mark all read</Button>
    </div>
    {reads.error && <p className="form-error" role="alert">{reads.error}</p>}
    {first.isError && <p className="form-error" role="alert">{first.error instanceof AlertApiError ? first.error.message : 'History could not be loaded.'}</p>}
    {first.isPending && <p role="status">Loading notifications…</p>}
    {first.isSuccess && items.length === 0 && <Card className="placeholder"><p className="muted">No notifications match these filters.</p></Card>}
    <ul className="notification-list">{items.map(item => <li key={item.id} className={item.readAt ? 'read' : 'unread'}>
      <SeverityBadge severity={item.severity} />
      <div><Link className="notification-title" to={notificationLink(item)} onClick={() => void reads.markOne(item)}>{item.title}</Link> <Badge className="tone-neutral">{kindLabel[item.kind] ?? humanize(item.kind)}</Badge>
        {!item.readAt && <Badge className="tone-warn">Unread</Badge>}
        <p>{item.body}</p>
        <small className="muted">{item.propertyName} · {formatMoment(item.createdAt, property.timeZone)} · incident {item.incidentStatus}
          {item.suppressedReason ? ` · not delivered (${item.suppressedReason === 'rateLimited' ? 'rate limited' : 'suppressed by rule policy'})` : ''}
          {!item.inApp && !item.suppressedReason ? ' · not shown in the app by your settings' : ''}
          {item.deliveries.map(delivery => ` · ${humanize(delivery.channel)} ${humanize(delivery.status).toLowerCase()}`).join('')}</small>
      </div>
      {!item.readAt && <Button variant="ghost" aria-label={`Mark “${item.title}” read`} onClick={() => void reads.markOne(item)}>Mark read</Button>}
    </li>)}</ul>
    {next != null && <Button variant="outline" onClick={() => setPages([...pages, next])} disabled={more.isFetching}>Load more</Button>}
  </div>
}

function Settings() {
  const client = useQueryClient()
  const stored = useQuery({ queryKey: ['notifications', 'preferences'], queryFn: ({ signal }) => alertsApi.preferences(signal), retry: false })
  const [draft, setDraft] = useState<NotificationPreferences | null>(null)
  const [permission, setPermission] = useState(() => 'Notification' in window ? Notification.permission : 'unsupported')
  const save = useMutation({
    mutationFn: (preferences: NotificationPreferences) => alertsApi.savePreferences(preferences),
    onSuccess: async () => { await client.invalidateQueries({ queryKey: ['notifications', 'preferences'] }); setDraft(null) },
  })
  if (stored.isPending) return <p role="status">Loading settings…</p>
  if (stored.isError) return <p className="form-error" role="alert">Notification settings could not be loaded.</p>
  const value = draft ?? stored.data
  const severities: Severity[] = ['info', 'warning', 'critical']
  const setSeverity = (severity: Severity, patch: Partial<NotificationPreferences['info']>) =>
    setDraft({ ...value, [severity]: { ...value[severity], ...patch } })
  return <Card className="notification-settings">
    <h2>How you are notified</h2>
    <p className="muted">These settings apply to you in every property. Active Critical incidents always stay visible in the app.</p>
    <div className="table-scroll"><table>
      <thead><tr><th>Severity</th><th>In the app</th><th>Browser notification</th><th>Sound</th></tr></thead>
      <tbody>{severities.map(severity => <tr key={severity}>
        <td><SeverityBadge severity={severity} /></td>
        <td><input type="checkbox" aria-label={`${severityLabel[severity]} in the app`} checked={severity === 'critical' || value[severity].inApp}
          disabled={severity === 'critical'} onChange={event => setSeverity(severity, { inApp: event.target.checked })} />
          {severity === 'critical' && <small className="muted"> Always on</small>}</td>
        <td><input type="checkbox" aria-label={`${severityLabel[severity]} browser notification`} checked={value[severity].browser}
          onChange={event => setSeverity(severity, { browser: event.target.checked })} /></td>
        <td>{severity === 'critical' ? <input type="checkbox" aria-label="Critical sound" checked={!!value.critical.sound}
          onChange={event => setSeverity('critical', { sound: event.target.checked })} /> : '—'}</td>
      </tr>)}</tbody>
    </table></div>
    <fieldset className="rule-policy"><legend>Notify me when an incident…</legend>
      {([['activated', 'opens'], ['escalated', 'escalates'], ['deescalated', 'de-escalates'], ['resolved', 'recovers'], ['reminder', 'is still active (reminders)']] as const)
        .map(([key, label]) => <label key={key} className="check"><input type="checkbox" checked={value.events[key]}
          onChange={event => setDraft({ ...value, events: { ...value.events, [key]: event.target.checked } })} />{label}</label>)}
    </fieldset>
    <p>Browser permission: <strong>{permission === 'granted' ? 'Allowed' : permission === 'denied' ? 'Blocked in this browser'
      : permission === 'unsupported' ? 'Not supported by this browser' : 'Not asked yet'}</strong>. System notifications appear only while this tab is in
      the background.</p>
    <div className="filters">
      {permission === 'default' && <Button variant="outline"
        onClick={() => void Notification.requestPermission().then(setPermission)}>Allow browser notifications</Button>}
      <Button onClick={() => save.mutate(value)} disabled={save.isPending || draft === null}>{save.isPending ? 'Saving…' : 'Save settings'}</Button>
      {save.isSuccess && draft === null && <span role="status">Saved.</span>}
      {save.isError && <p className="form-error" role="alert">The settings could not be saved.</p>}
    </div>
  </Card>
}
