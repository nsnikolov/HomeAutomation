import { useMemo, useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import {
  aggregationLabel, AlertApiError, alertsApi, conditions, defaultPolicy, evidenceValue, operatorLabel, qualityLabel, ruleSummary, severityLabel,
  signalKey, type AlertRuleInput, type AlertRulePreview, type ConditionOperator, type RuleCondition, type RuleGroup, type Severity,
  type SignalCatalogEntry,
} from '../../shared/alerts'
import type { Property } from '../../shared/types'
import { Button, Card, Input } from '../../shared/ui'
import { formatMoment } from '../../shared/utils'
import { TruthBadge } from './AlertBits'

const maxDepth = 3
const maxConditions = 12

export function emptyRule(): AlertRuleInput {
  return {
    name: '', description: null, enabled: true, severity: 'warning', correlationKey: null, incidentTitle: null,
    definition: { operator: 'and', children: [] }, activationSeconds: 60, recoverySeconds: 120, notifications: { ...defaultPolicy },
  }
}

/** Problems the editor can explain before asking the server, which validates everything again. */
export function validateRule(input: AlertRuleInput): string[] {
  const errors: string[] = []
  if (!input.name.trim()) errors.push('Give the rule a name.')
  const all = conditions(input.definition)
  if (all.length === 0) errors.push('Add at least one condition.')
  if (all.length > maxConditions) errors.push(`A rule can have at most ${maxConditions} conditions.`)
  all.forEach((condition, index) => {
    const label = `Condition ${index + 1}`
    if (condition.signals.length === 0) errors.push(`${label}: choose a signal.`)
    const numeric = ['lessThan', 'lessThanOrEqual', 'greaterThan', 'greaterThanOrEqual', 'equals', 'notEquals'].includes(condition.operator) &&
      condition.text == null
    if (numeric && condition.signals[0]?.kind === 'measurement' && (condition.number == null || !Number.isFinite(condition.number)))
      errors.push(`${label}: enter a threshold.`)
    if (condition.recoveryNumber != null && condition.number != null && (
      (condition.operator.startsWith('less') && condition.recoveryNumber < condition.number) ||
      (condition.operator.startsWith('greater') && condition.recoveryNumber > condition.number)))
      errors.push(`${label}: the recovery threshold must be on the recovered side of the activation threshold.`)
    if ((condition.operator === 'eventOccurred' || condition.operator === 'stateChanged') && !condition.windowSeconds)
      errors.push(`${label}: choose how far back to look.`)
  })
  if (input.correlationKey && !/^[a-z][a-z0-9._:-]{0,79}$/.test(input.correlationKey))
    errors.push('Use a correlation group such as grid.outage: lower-case letters, digits, dots, dashes and colons.')
  return errors
}

type Props = {
  property: Property; catalog: SignalCatalogEntry[]; initial: AlertRuleInput; ruleId?: number; revision?: number
  onSaved: (message: string) => void; onCancel: () => void
}

export function RuleEditor({ property, catalog, initial, ruleId, revision, onSaved, onCancel }: Props) {
  const [input, setInput] = useState<AlertRuleInput>(() => ({ ...initial, notifications: initial.notifications ?? { ...defaultPolicy } }))
  const [shownErrors, setShownErrors] = useState<string[]>([])
  const [preview, setPreview] = useState<AlertRulePreview | null>(null)
  const labels = useMemo(() => new Map(catalog.map(entry => [entry.key, entry.label])), [catalog])
  const errors = validateRule(input)
  const policy = input.notifications ?? defaultPolicy
  const save = useMutation({
    mutationFn: () => ruleId === undefined ? alertsApi.createRule(property.id, input)
      : alertsApi.updateRule(property.id, ruleId, { ...input, expectedRevision: revision }),
    onSuccess: () => onSaved(ruleId === undefined ? `Rule “${input.name.trim()}” created.` : `Rule “${input.name.trim()}” saved.`),
  })
  const evaluate = useMutation({ mutationFn: () => alertsApi.preview(property.id, input), onSuccess: setPreview })
  const update = (patch: Partial<AlertRuleInput>) => { setInput(current => ({ ...current, ...patch })); setPreview(null) }
  const nextId = () => {
    const used = new Set(conditions(input.definition).map(condition => condition.id))
    let index = 1
    while (used.has(`c${index}`)) index++
    return `c${index}`
  }

  function submit() {
    if (errors.length) { setShownErrors(errors); return }
    setShownErrors([])
    save.mutate()
  }

  return <Card className="rule-editor">
    <h2>{ruleId === undefined ? 'New alert rule' : `Edit ${initial.name}`}</h2>
    <p className="muted">Alert rules create incidents and notifications. They never switch or control equipment.</p>
    <div className="rule-fields">
      <label>Name<Input value={input.name} maxLength={120} onChange={event => update({ name: event.target.value })} /></label>
      <label>Severity<select className="input select-input" value={input.severity}
        onChange={event => update({ severity: event.target.value as Severity })}>
        {(['info', 'warning', 'critical'] as const).map(value => <option key={value} value={value}>{severityLabel[value]}</option>)}
      </select></label>
      <label className="wide">Description<Input value={input.description ?? ''} maxLength={500}
        onChange={event => update({ description: event.target.value || null })} /></label>
      <label>Correlation group<Input value={input.correlationKey ?? ''} placeholder="for example grid.outage" maxLength={80}
        aria-describedby="correlation-help" onChange={event => update({ correlationKey: event.target.value.trim().toLowerCase() || null })} /></label>
      <label>Incident title<Input value={input.incidentTitle ?? ''} placeholder="for example Grid power unavailable" maxLength={120}
        onChange={event => update({ incidentTitle: event.target.value || null })} /></label>
      <p id="correlation-help" className="muted wide">Rules with the same correlation group share one incident, which takes the highest active severity.
        Leave it empty for an independent incident.</p>
      <label>Confirm for (seconds)<Input type="number" min={0} max={86400} value={input.activationSeconds}
        onChange={event => update({ activationSeconds: Number(event.target.value) })} /></label>
      <label>Recover after (seconds)<Input type="number" min={0} max={86400} value={input.recoverySeconds}
        onChange={event => update({ recoverySeconds: Number(event.target.value) })} /></label>
      <label className="check"><input type="checkbox" checked={input.enabled} onChange={event => update({ enabled: event.target.checked })} />Enabled</label>
    </div>

    <h3>Conditions</h3>
    <GroupEditor group={input.definition} depth={1} catalog={catalog} nextId={nextId}
      onChange={definition => update({ definition })} />

    <h3>Notifications</h3>
    <fieldset className="rule-policy">
      <legend className="sr-only">Which changes notify</legend>
      {([['onActivation', 'When the incident opens'], ['onEscalation', 'When it escalates'], ['onDeescalation', 'When it de-escalates'],
        ['onResolution', 'When it recovers']] as const).map(([key, label]) =>
        <label key={key} className="check"><input type="checkbox" checked={policy[key]}
          onChange={event => update({ notifications: { ...policy, [key]: event.target.checked } })} />{label}</label>)}
      <label>Remind every (minutes, 0 = never)<Input type="number" min={0} max={1440} value={policy.reminderMinutes}
        onChange={event => update({ notifications: { ...policy, reminderMinutes: Number(event.target.value) } })} /></label>
      <label>Do not notify while this group is active<Input value={policy.suppressWhileActive ?? ''} placeholder="optional, for example grid.outage"
        onChange={event => update({ notifications: { ...policy, suppressWhileActive: event.target.value.trim().toLowerCase() || null } })} /></label>
    </fieldset>

    <p className="rule-summary" aria-live="polite">{conditions(input.definition).length > 0 ? ruleSummary(input, labels) : 'Add a condition to see a summary.'}</p>
    {shownErrors.length > 0 && <ul className="form-error" role="alert">{shownErrors.map(error => <li key={error}>{error}</li>)}</ul>}
    {save.isError && <p className="form-error" role="alert">{save.error instanceof AlertApiError ? save.error.message : 'The rule could not be saved.'}</p>}
    <div className="filters">
      <Button onClick={submit} disabled={save.isPending}>{save.isPending ? 'Saving…' : 'Save rule'}</Button>
      <Button variant="outline" onClick={() => evaluate.mutate()} disabled={evaluate.isPending || errors.length > 0}>
        {evaluate.isPending ? 'Checking…' : 'Preview with latest readings'}</Button>
      <Button variant="ghost" onClick={onCancel}>Cancel</Button>
    </div>
    {evaluate.isError && <p className="form-error" role="alert">The preview is unavailable.</p>}
    {preview && <RulePreview preview={preview} property={property} />}
  </Card>
}

function GroupEditor({ group, depth, catalog, nextId, onChange, onRemove }: {
  group: RuleGroup; depth: number; catalog: SignalCatalogEntry[]; nextId: () => string; onChange: (group: RuleGroup) => void
  onRemove?: () => void
}) {
  const setChild = (index: number, node: RuleGroup['children'][number] | null) =>
    onChange({ ...group, children: node ? group.children.map((child, at) => at === index ? node : child) : group.children.filter((_, at) => at !== index) })
  const first = catalog[0]
  return <div className={`rule-group depth-${depth}`} role="group" aria-label={depth === 1 ? 'Rule conditions' : 'Condition group'}>
    <div className="filters">
      <label>Match<select className="input select-input" value={group.operator}
        onChange={event => onChange({ ...group, operator: event.target.value as 'and' | 'or' })}>
        <option value="and">All of these (AND)</option><option value="or">Any of these (OR)</option>
      </select></label>
      {onRemove && <Button variant="ghost" onClick={onRemove}>Remove group</Button>}
    </div>
    {group.children.map((child, index) => <div key={child.condition?.id ?? `group-${index}`} className="rule-node">
      {index > 0 && <span className="rule-joiner">{group.operator.toUpperCase()}</span>}
      {child.condition
        ? <ConditionEditor condition={child.condition} catalog={catalog} onChange={condition => setChild(index, { condition })}
          onRemove={() => setChild(index, null)} />
        : child.group && <GroupEditor group={child.group} depth={depth + 1} catalog={catalog} nextId={nextId}
          onChange={nested => setChild(index, { group: nested })} onRemove={() => setChild(index, null)} />}
    </div>)}
    <div className="filters">
      <Button variant="outline" disabled={!first || group.children.length >= 8}
        onClick={() => onChange({ ...group, children: [...group.children, { condition: conditionFor(first!, nextId()) }] })}>Add condition</Button>
      {depth < maxDepth && <Button variant="ghost" disabled={!first || group.children.length >= 8}
        onClick={() => onChange({ ...group, children: [...group.children, {
          group: { operator: group.operator === 'and' ? 'or' : 'and', children: [{ condition: conditionFor(first!, nextId()) }] },
        }] })}>Add group</Button>}
    </div>
    {!first && <p className="muted">This property has no signals yet. Rules can use devices once they report data.</p>}
  </div>
}

/** A new condition for a signal, with the first operator its type supports and the suggested maximum reading age. */
export function conditionFor(entry: SignalCatalogEntry, id: string): RuleCondition {
  const operator = entry.operators[0]
  return {
    id, signals: [entry.signal], aggregation: 'single', operator, unit: entry.unit,
    number: entry.valueType === 'number' ? 0 : null,
    text: entry.valueType === 'status' && (operator === 'equals' || operator === 'notEquals') ? entry.states[0] ?? entry.text ?? '' : null,
    recoveryNumber: null, freshnessSeconds: entry.suggestedFreshnessSeconds,
    windowSeconds: operator === 'eventOccurred' || operator === 'stateChanged' ? 900 : null,
  }
}

function ConditionEditor({ condition, catalog, onChange, onRemove }: {
  condition: RuleCondition; catalog: SignalCatalogEntry[]; onChange: (condition: RuleCondition) => void; onRemove: () => void
}) {
  const byKey = new Map(catalog.map(entry => [entry.key, entry]))
  const primary = condition.signals[0] ? byKey.get(signalKey(condition.signals[0])) : undefined
  const compatible = primary ? catalog.filter(entry => entry.key !== primary.key && entry.signal.kind === primary.signal.kind &&
    entry.signal.metric === primary.signal.metric && entry.signal.eventCode === primary.signal.eventCode) : []
  const selected = new Set(condition.signals.map(signalKey))
  const operator = condition.operator
  const numeric = primary?.valueType === 'number'
  const status = primary?.valueType === 'status' && (operator === 'equals' || operator === 'notEquals')
  const windowed = operator === 'eventOccurred' || operator === 'stateChanged'
  const ordered = operator.startsWith('less') || operator.startsWith('greater')

  function choose(key: string) {
    const entry = byKey.get(key)
    if (entry) onChange(conditionFor(entry, condition.id))
  }
  function toggleExtra(entry: SignalCatalogEntry, on: boolean) {
    const signals = on ? [...condition.signals, entry.signal] : condition.signals.filter(signal => signalKey(signal) !== entry.key)
    onChange({ ...condition, signals, aggregation: signals.length > 1 ? (condition.aggregation === 'single' ? 'all' : condition.aggregation) : 'single' })
  }
  function setOperator(value: ConditionOperator) {
    onChange({
      ...condition, operator: value, recoveryNumber: value.startsWith('less') || value.startsWith('greater') ? condition.recoveryNumber : null,
      windowSeconds: value === 'eventOccurred' || value === 'stateChanged' ? condition.windowSeconds ?? 900 : null,
      text: primary?.valueType === 'status' && (value === 'equals' || value === 'notEquals') ? condition.text ?? primary.states[0] ?? '' : null,
    })
  }

  return <div className="rule-condition" role="group" aria-label={`Condition ${condition.id}`}>
    <label className="wide">Signal<select className="input select-input" value={primary?.key ?? ''} onChange={event => choose(event.target.value)}>
      {!primary && <option value="">The selected device is no longer available</option>}
      {catalog.map(entry => <option key={entry.key} value={entry.key}>{entry.label}</option>)}
    </select></label>
    {compatible.length > 0 && <fieldset className="wide rule-extra">
      <legend>Also check</legend>
      {compatible.map(entry => <label key={entry.key} className="check"><input type="checkbox" checked={selected.has(entry.key)}
        onChange={event => toggleExtra(entry, event.target.checked)} />{entry.label}</label>)}
      {condition.signals.length > 1 && <label>Combine<select className="input select-input" value={condition.aggregation}
        onChange={event => onChange({ ...condition, aggregation: event.target.value as 'any' | 'all' })}>
        <option value="all">True when {aggregationLabel.all} matches</option>
        <option value="any">True when {aggregationLabel.any} matches</option>
      </select></label>}
    </fieldset>}
    <label>Comparison<select className="input select-input" value={operator} onChange={event => setOperator(event.target.value as ConditionOperator)}>
      {(primary?.operators ?? [operator]).map(value => <option key={value} value={value}>{operatorLabel[value]}</option>)}
    </select></label>
    {numeric && !windowed && <label>Threshold{condition.unit ? ` (${condition.unit})` : ''}<Input type="number" step="any"
      value={condition.number ?? ''} onChange={event => onChange({ ...condition, number: event.target.value === '' ? null : Number(event.target.value) })} /></label>}
    {numeric && ordered && <label>Recovers {operator.startsWith('less') ? 'above' : 'below'}{condition.unit ? ` (${condition.unit})` : ''}
      <Input type="number" step="any" placeholder="same as threshold" value={condition.recoveryNumber ?? ''}
        onChange={event => onChange({ ...condition, recoveryNumber: event.target.value === '' ? null : Number(event.target.value) })} /></label>}
    {status && (primary?.states.length
      ? <label>State<select className="input select-input" value={condition.text ?? ''} onChange={event => onChange({ ...condition, text: event.target.value })}>
        {primary.states.map(state => <option key={state} value={state}>{state}</option>)}</select></label>
      : <label>State<Input value={condition.text ?? ''} maxLength={32} onChange={event => onChange({ ...condition, text: event.target.value })} /></label>)}
    {windowed && <label>Within the last (minutes)<Input type="number" min={1} max={1440} value={(condition.windowSeconds ?? 900) / 60}
      onChange={event => onChange({ ...condition, windowSeconds: Math.round(Number(event.target.value) * 60) })} /></label>}
    {operator !== 'eventOccurred' && <label>Ignore readings older than (minutes)<Input type="number" min={1} max={10080}
      value={Math.round(condition.freshnessSeconds / 60)}
      onChange={event => onChange({ ...condition, freshnessSeconds: Math.max(30, Math.round(Number(event.target.value) * 60)) })} /></label>}
    {primary && <p className="muted wide">{primary.provider} reports about every {Math.round(primary.samplingSeconds / 60)} min. Older readings make
      this condition unknown, never false.</p>}
    <Button variant="ghost" onClick={onRemove}>Remove condition</Button>
  </div>
}

export function RulePreview({ preview, property }: { preview: AlertRulePreview; property: Property }) {
  if (!preview.valid) return <ul className="form-error" role="alert">{preview.errors.map(error => <li key={error}>{error}</li>)}</ul>
  return <section className="rule-preview" aria-label="Rule preview">
    <p>Overall result now: <TruthBadge value={preview.result} /> <span className="muted">Evaluated {formatMoment(preview.evaluatedAt, property.timeZone)}.
      A preview never creates incidents or notifications.</span></p>
    <div className="table-scroll"><table>
      <thead><tr><th>Condition</th><th>Signal</th><th>Value</th><th>Observed</th><th>Freshness</th><th>Result</th></tr></thead>
      <tbody>{preview.conditions.flatMap(condition => condition.signals.map((signal, index) => <tr key={`${condition.conditionId}-${index}`}>
        <td>{index === 0 ? condition.conditionId : ''}</td><td>{signal.label}</td><td>{evidenceValue(signal)}</td>
        <td>{signal.observedAt ? formatMoment(signal.observedAt, property.timeZone) : '—'}</td>
        <td>{qualityLabel[signal.quality]}{signal.reason ? ` · ${signal.reason}` : ''}</td>
        <td><TruthBadge value={signal.result} />{index === 0 && condition.signals.length > 1 ? <> condition: <TruthBadge value={condition.result} /></> : null}</td>
      </tr>))}</tbody>
    </table></div>
  </section>
}
