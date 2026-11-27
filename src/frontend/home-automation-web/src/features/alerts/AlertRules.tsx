import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router'
import {
  AlertApiError, alertsApi, groupText, ruleSummary, signalIndex, type AlertRuleInput, type AlertRuleView, type Severity,
} from '../../shared/alerts'
import { ActionMenu } from '../../shared/ActionMenu'
import { ConfirmDialog } from '../../shared/ConfirmDialog'
import type { Property } from '../../shared/types'
import { Badge, Button, Card } from '../../shared/ui'
import { formatMoment } from '../../shared/utils'
import { SeverityBadge } from './AlertBits'
import { emptyRule, RuleEditor } from './RuleEditor'

type Editing = { input: AlertRuleInput; ruleId?: number; revision?: number } | null
type StatusFilter = '' | 'enabled' | 'disabled' | 'active'

/** Automations area: alert rules. Device actions are a separate, future feature; alert rules never control equipment. */
export function AutomationsPage({ property, properties }: { property: Property; properties: Property[] }) {
  return <section aria-label="Automations">
    <div className="page-heading"><h1>Automations</h1>
      <p>Alert rules watch telemetry and create incidents and notifications. They never switch or control equipment.</p></div>
    <AlertRules property={property} properties={properties} />
    <Card className="placeholder"><h2>Device actions</h2>
      <p className="muted">Automatic device actions are not available yet. Equipment control stays on the provider pages.</p></Card>
  </section>
}

export function AlertRules({ property, properties }: { property: Property; properties: Property[] }) {
  const client = useQueryClient()
  const [propertyId, setPropertyId] = useState(property.id)
  const selected = properties.find(item => item.id === propertyId) ?? property
  const [severity, setSeverity] = useState<'' | Severity>('')
  const [status, setStatus] = useState<StatusFilter>('')
  const [editing, setEditing] = useState<Editing>(null)
  const [templatesOpen, setTemplatesOpen] = useState(false)
  const [gridSetup, setGridSetup] = useState(false)
  const [deleting, setDeleting] = useState<AlertRuleView | null>(null)
  const [notice, setNotice] = useState('')
  const key = ['property', selected.id, 'alerts']
  const list = useQuery({ queryKey: [...key, 'rules'], queryFn: ({ signal }) => alertsApi.rules(selected.id, signal), retry: false })
  const catalog = useQuery({ queryKey: [...key, 'catalog'], queryFn: ({ signal }) => alertsApi.catalog(selected.id, signal), retry: false,
    enabled: list.isSuccess })
  const templates = useQuery({ queryKey: [...key, 'templates'], queryFn: ({ signal }) => alertsApi.templates(selected.id, signal), retry: false,
    enabled: list.isSuccess && !!list.data?.canManage })
  const labels = useMemo(() => signalIndex(catalog.data ?? []).labels, [catalog.data])
  // Rules, signals and drafts belong to one property; switching starts from that property's own list.
  const switchProperty = (id: number) => {
    setPropertyId(id); setEditing(null); setTemplatesOpen(false); setGridSetup(false); setDeleting(null); setNotice('')
  }
  const refresh = () => client.invalidateQueries({ queryKey: key })
  const toggle = useMutation({
    mutationFn: (rule: AlertRuleView) => alertsApi.setEnabled(selected.id, rule.id, !rule.enabled, rule.revision),
    onSuccess: (_, rule) => { setNotice(`Rule “${rule.name}” ${rule.enabled ? 'disabled' : 'enabled'}.`); return refresh() },
    onError: error => setNotice(error instanceof AlertApiError ? error.message : 'The rule could not be changed.'),
  })
  const duplicate = useMutation({
    mutationFn: (rule: AlertRuleView) => alertsApi.duplicateRule(selected.id, rule.id),
    onSuccess: (_, rule) => { setNotice(`A disabled copy of “${rule.name}” was created.`); return refresh() },
    onError: error => setNotice(error instanceof AlertApiError ? error.message : 'The rule could not be duplicated.'),
  })

  if (list.isPending) return <p role="status">Loading alert rules…</p>
  if (list.isError) return <Card className="placeholder"><p className="muted">{list.error instanceof AlertApiError ? list.error.message
    : 'Alert rules could not be loaded.'}</p></Card>

  const canManage = list.data.canManage
  const rules = list.data.rules.filter(rule => (!severity || rule.severity === severity) && (!status ||
    (status === 'enabled' && rule.enabled) || (status === 'disabled' && !rule.enabled) || (status === 'active' && rule.status.incidentStatus === 'active')))

  if (editing && catalog.data) return <RuleEditor key={editing.ruleId ?? 'new'} property={selected} catalog={catalog.data} initial={editing.input}
    ruleId={editing.ruleId} revision={editing.revision}
    onCancel={() => setEditing(null)} onSaved={message => { setEditing(null); setNotice(message); void refresh() }} />

  return <section aria-label="Alert rules" className="alert-rules">
    <div className="filters">
      <h2>Alert rules</h2>
      <label>Property<select className="input select-input" value={selected.id} onChange={event => switchProperty(Number(event.target.value))}>
        {properties.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>
      <label>Severity<select className="input select-input" value={severity} onChange={event => setSeverity(event.target.value as '' | Severity)}>
        <option value="">All</option><option value="info">Info</option><option value="warning">Warning</option><option value="critical">Critical</option>
      </select></label>
      <label>Status<select className="input select-input" value={status} onChange={event => setStatus(event.target.value as StatusFilter)}>
        <option value="">All</option><option value="enabled">Enabled</option><option value="disabled">Disabled</option>
        <option value="active">Incident active</option>
      </select></label>
      {canManage && <>
        <Button disabled={!catalog.data} onClick={() => { setNotice(''); setEditing({ input: emptyRule() }) }}>New rule</Button>
        <Button variant="outline" aria-expanded={templatesOpen} onClick={() => setTemplatesOpen(!templatesOpen)}>Templates</Button>
        {templates.data?.gridOutageSetup && <Button variant="outline" onClick={() => setGridSetup(true)}>Set up grid outage alerts</Button>}
      </>}
    </div>
    {notice && <p role="status" className="notice">{notice}</p>}
    {!canManage && <p className="muted">You can view rules. Owners, Admins and Operators can change them.</p>}
    {templatesOpen && canManage && <Templates templates={templates.data?.templates}
      onUse={draft => { setTemplatesOpen(false); setEditing({ input: { ...draft, enabled: false } }) }} />}

    {rules.length === 0 ? <Card className="placeholder"><p className="muted">{list.data.rules.length === 0
      ? 'No alert rules yet. Start from a template or create a rule.' : 'No rules match these filters.'}</p></Card>
      : <div className="table-scroll"><table className="rules-table">
        <thead><tr><th>Name</th><th>Property</th><th>Enabled</th><th>Severity</th><th>Conditions</th><th>Last triggered</th><th>Incident</th>
          <th><span className="sr-only">Actions</span></th></tr></thead>
        <tbody>{rules.map(rule => <tr key={rule.id}>
          <td><strong>{rule.name}</strong>{rule.correlationKey && <small className="muted"> · {rule.correlationKey}</small>}
            {rule.warnings.map(warning => <p key={warning} className="form-error">{warning}</p>)}</td>
          <td>{selected.name}</td>
          <td>{canManage
            ? <button type="button" role="switch" aria-checked={rule.enabled} aria-label={`${rule.name} enabled`} className="switch"
              disabled={toggle.isPending} onClick={() => toggle.mutate(rule)}>{rule.enabled ? 'On' : 'Off'}</button>
            : rule.enabled ? 'On' : 'Off'}</td>
          <td><SeverityBadge severity={rule.severity} /></td>
          <td className="rule-summary-cell">{groupText(rule.definition, labels)}</td>
          <td>{rule.status.lastTriggeredAt ? formatMoment(rule.status.lastTriggeredAt, selected.timeZone) : 'Never'}</td>
          <td>{rule.status.incidentStatus === 'active' && rule.status.incidentSeverity
            ? <Link to={`/properties/${selected.id}/alerts?incident=${rule.status.incidentId}`}><SeverityBadge severity={rule.status.incidentSeverity} /> Active</Link>
            : rule.status.incidentStatus === 'resolved' ? <Badge className="tone-neutral">Resolved</Badge>
              : rule.enabled ? rule.status.result === 'unknown' && rule.status.lastEvaluatedAt ? 'Inputs unknown' : 'Clear' : '—'}</td>
          <td>{canManage && <ActionMenu label={`Actions for ${rule.name}`} items={[
            { label: 'Edit…', onSelect: () => { setNotice(''); setEditing({ input: { ...rule }, ruleId: rule.id, revision: rule.revision }) } },
            { label: 'Duplicate', onSelect: () => duplicate.mutate(rule) },
            { label: 'Delete…', destructive: true, onSelect: () => setDeleting(rule) },
          ]} />}</td>
        </tr>)}</tbody>
      </table></div>}

    <ConfirmDialog open={deleting !== null} title={`Delete “${deleting?.name ?? ''}”?`} confirmLabel="Delete rule" destructive
      onCancel={() => setDeleting(null)}
      onConfirm={async () => {
        await alertsApi.deleteRule(selected.id, deleting!.id, deleting!.revision)
        setNotice(`Rule “${deleting!.name}” deleted.`)
        setDeleting(null)
        await refresh()
      }}
      errorText={error => error instanceof AlertApiError ? error.message : 'The rule could not be deleted.'}>
      <ul>
        <li>The rule stops evaluating. An incident it keeps open closes unless another rule in its group is still active.</li>
        <li>Past incidents, their timelines and notifications are kept.</li>
      </ul>
    </ConfirmDialog>
    <ConfirmDialog open={gridSetup} title="Set up grid outage alerts?" confirmLabel="Create and enable both rules"
      blocked={!catalog.data} onCancel={() => setGridSetup(false)}
      onConfirm={async () => {
        await alertsApi.createRules(selected.id, templates.data!.gridOutageSetup!)
        setNotice('Grid outage alerts are on. Review or adjust them in the list.')
        setGridSetup(false)
        await refresh()
      }}
      errorText={error => error instanceof AlertApiError ? error.message : 'The rules could not be created.'}>
      <p>These two rules share one grid outage incident: it opens as Warning and escalates to Critical when the battery is low.</p>
      {catalog.data
        ? <ul>{templates.data?.gridOutageSetup?.map(rule => <li key={rule.name}><strong>{rule.name}</strong>: {ruleSummary(rule, labels)}</li>)}</ul>
        : <p role="status">{catalog.isError ? 'Signal names could not be loaded, so the rules cannot be reviewed yet.' : 'Loading signal names…'}</p>}
    </ConfirmDialog>
  </section>
}

function Templates({ templates, onUse }: {
  templates?: { code: string; name: string; description: string; available: boolean; unavailable: string | null
    draft: AlertRuleInput | null }[]; onUse: (draft: AlertRuleInput) => void
}) {
  if (!templates) return <p role="status">Loading templates…</p>
  return <div className="template-grid">{templates.map(template => <Card key={template.code} className="template-card">
    <h3>{template.name}</h3><p>{template.description}</p>
    {template.available && template.draft
      ? <Button variant="outline" onClick={() => onUse(template.draft!)}>Review and use</Button>
      : <p className="muted">{template.unavailable}</p>}
  </Card>)}</div>
}
