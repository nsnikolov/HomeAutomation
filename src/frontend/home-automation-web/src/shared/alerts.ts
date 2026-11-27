// Alert rules, incidents and notifications. Rules only create incidents and notifications; nothing here controls equipment.
import type { Metric } from './types'

export type Severity = 'info' | 'warning' | 'critical'
export type Truth = 'true' | 'false' | 'unknown'
export type ConditionOperator = 'equals' | 'notEquals' | 'lessThan' | 'lessThanOrEqual' | 'greaterThan' | 'greaterThanOrEqual'
  | 'isTrue' | 'isFalse' | 'eventOccurred' | 'stateChanged'
export type SignalKind = 'measurement' | 'connectivity' | 'deviceEvent'
export type Aggregation = 'single' | 'any' | 'all'
export type ValueType = 'number' | 'boolean' | 'status' | 'event'
export type RulePhase = 'inactive' | 'pending' | 'active' | 'recovering'
export type NotificationKind = 'activated' | 'escalated' | 'deescalated' | 'resolved' | 'reminder'

export type SignalRef = {
  kind: SignalKind; deviceId?: number | null; endpointKey?: string | null; installationId?: number | null; metric?: Metric | null
  eventCode?: string | null
}
export type RuleCondition = {
  id: string; signals: SignalRef[]; aggregation: Aggregation; operator: ConditionOperator; number?: number | null; text?: string | null
  unit?: string | null; recoveryNumber?: number | null; freshnessSeconds: number; windowSeconds?: number | null
}
export type RuleNode = { condition?: RuleCondition | null; group?: RuleGroup | null }
export type RuleGroup = { operator: 'and' | 'or'; children: RuleNode[] }
export type NotificationPolicy = {
  onActivation: boolean; onEscalation: boolean; onDeescalation: boolean; onResolution: boolean; reminderMinutes: number
  suppressWhileActive?: string | null
}
export type AlertRuleInput = {
  name: string; description?: string | null; enabled: boolean; severity: Severity; correlationKey?: string | null
  incidentTitle?: string | null; definition: RuleGroup; activationSeconds: number; recoverySeconds: number
  notifications?: NotificationPolicy | null; templateCode?: string | null; expectedRevision?: number | null
}
export type AlertRuleView = Omit<AlertRuleInput, 'expectedRevision'> & {
  id: number; propertyId: number; notifications: NotificationPolicy; revision: number; createdAt: string; updatedAt: string
  status: {
    phase: RulePhase; result: Truth; lastEvaluatedAt: string | null; lastTriggeredAt: string | null; incidentId: number | null
    incidentSeverity: Severity | null; incidentStatus: 'active' | 'resolved' | null
  }
  warnings: string[]
}
export type AlertRuleList = { canManage: boolean; rules: AlertRuleView[] }
export type SignalCatalogEntry = {
  key: string; signal: SignalRef; label: string; deviceId: number | null; deviceName: string | null; endpointName: string | null
  provider: string; valueType: ValueType; unit: string | null; operators: ConditionOperator[]; states: string[]
  samplingSeconds: number; suggestedFreshnessSeconds: number; number: number | null; flag: boolean | null; text: string | null
  observedAt: string | null
}
export type AlertTemplate = { code: string; name: string; description: string; available: boolean; unavailable: string | null; draft: AlertRuleInput | null }
export type AlertTemplateList = { templates: AlertTemplate[]; gridOutageSetup: AlertRuleInput[] | null }
export type SignalEvidence = {
  signal: SignalRef; label: string; result: Truth; quality: 'fresh' | 'stale' | 'missing' | 'invalid' | 'unavailable'
  number: number | null; flag: boolean | null; text: string | null; observedAt: string | null; events: number; reason: string | null
}
export type ConditionEvidence = { conditionId: string; result: Truth; signals: SignalEvidence[] }
export type AlertRulePreview = { valid: boolean; errors: string[]; result: Truth; conditions: ConditionEvidence[]; evaluatedAt: string }
export type IncidentRule = {
  ruleId: number; ruleName: string; revision: number; severity: Severity; active: boolean; firstActiveAt: string; lastActiveAt: string
  clearedAt: string | null; clearedReason: string | null
}
export type Incident = {
  id: number; propertyId: number; propertyName: string; title: string; correlationKey: string; status: 'active' | 'resolved'
  severity: Severity; originalSeverity: Severity; maxSeverity: Severity; startedAt: string; lastChangedAt: string; resolvedAt: string | null
  resolution: string | null; acknowledgedAt: string | null; rules: IncidentRule[]
}
export type IncidentEvent = {
  id: number; occurredAt: string; observedAt: string | null; kind: string; ruleId: number | null; ruleRevision: number | null
  fromSeverity: Severity | null; toSeverity: Severity | null
  detail: { ruleName?: string | null; evidence?: ConditionEvidence[] | null; reason?: string | null; durationSeconds?: number | null } | null
}
export type IncidentList = { canAcknowledge: boolean; incidents: Incident[] }
export type IncidentDetail = { incident: Incident; timeline: IncidentEvent[]; canAcknowledge: boolean }
export type Delivery = { channel: string; status: string; reason: string | null; updatedAt: string }
export type NotificationItem = {
  id: number; propertyId: number; propertyName: string; incidentId: number; kind: NotificationKind; severity: Severity; title: string
  body: string; createdAt: string; inApp: boolean; readAt: string | null; suppressedReason: string | null
  incidentStatus: 'active' | 'resolved'; incidentSeverity: Severity; deliveries: Delivery[]
}
export type NotificationPage = { items: NotificationItem[]; next: number | null }
export type NotificationSummary = { unread: number; unreadCritical: number; unreadWarning: number; activeCritical: Incident[] }
export type SeverityPreference = { inApp: boolean; browser: boolean; sound?: boolean }
export type NotificationPreferences = {
  info: SeverityPreference; warning: SeverityPreference; critical: SeverityPreference
  events: { activated: boolean; escalated: boolean; deescalated: boolean; resolved: boolean; reminder: boolean }
}
export type BrowserNotification = {
  notificationId: number; propertyId: number; incidentId: number; kind: NotificationKind; severity: Severity; title: string; body: string
  createdAt: string; sound: boolean
}
export type IncidentFilters = { status?: string; severity?: string; ruleId?: number; deviceId?: number; from?: string; to?: string }
export type NotificationFilters = {
  propertyId?: number; severity?: string; kind?: string; ruleId?: number; deviceId?: number; incidentStatus?: string; unread?: boolean
  inApp?: boolean; from?: string; to?: string; before?: number; limit?: number
}

/** Problem responses carry a readable title; these fallbacks cover responses without one. */
export class AlertApiError extends Error {
  constructor(public readonly status: number, public readonly code: string | undefined, message: string) {
    super(message)
  }
}

async function call<T>(path: string, options: RequestInit = {}): Promise<T> {
  const response = await fetch(`/api/v1${path}`, {
    ...options, headers: { Accept: 'application/json', ...options.headers }, credentials: 'same-origin',
  })
  if (!response.ok) {
    const problem: { title?: unknown; code?: unknown } | null = await response.json().catch(() => null)
    const fallback = response.status === 503 ? 'Alerts are not available until the database is upgraded.'
      : response.status === 403 ? 'Only property Owners, Admins and Operators can change alert rules or acknowledge incidents.'
        : response.status === 409 ? 'Someone changed this in the meantime. Reload and try again.' : `Request failed (${response.status})`
    throw new AlertApiError(response.status, typeof problem?.code === 'string' ? problem.code : undefined,
      typeof problem?.title === 'string' && problem.title ? problem.title : fallback)
  }
  return response.status === 204 ? undefined as T : await response.json() as T
}

async function send<T>(path: string, method: 'POST' | 'PUT' | 'DELETE', body?: object) {
  const token = await call<{ token: string }>('/session/csrf')
  return call<T>(path, {
    method, headers: { 'X-CSRF-TOKEN': token.token, ...(body && { 'Content-Type': 'application/json' }) },
    body: body && JSON.stringify(body),
  })
}

function query(values: Record<string, string | number | boolean | undefined>) {
  const params = new URLSearchParams()
  for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== '') params.set(key, String(value))
  const text = params.toString()
  return text ? `?${text}` : ''
}

const rules = (property: number) => `/properties/${property}/alert-rules`

export const alertsApi = {
  rules: (property: number, signal?: AbortSignal) => call<AlertRuleList>(rules(property), { signal }),
  rule: (property: number, rule: number, signal?: AbortSignal) => call<AlertRuleView>(`${rules(property)}/${rule}`, { signal }),
  catalog: (property: number, signal?: AbortSignal) => call<SignalCatalogEntry[]>(`${rules(property)}/catalog`, { signal }),
  templates: (property: number, signal?: AbortSignal) => call<AlertTemplateList>(`${rules(property)}/templates`, { signal }),
  preview: (property: number, input: AlertRuleInput) => send<AlertRulePreview>(`${rules(property)}/preview`, 'POST', input),
  createRule: (property: number, input: AlertRuleInput) => send<{ id: number }>(rules(property), 'POST', input),
  createRules: (property: number, inputs: AlertRuleInput[]) => send<{ id: number }>(`${rules(property)}/batch`, 'POST', { rules: inputs }),
  updateRule: (property: number, rule: number, input: AlertRuleInput) => send<{ id: number }>(`${rules(property)}/${rule}`, 'PUT', input),
  setEnabled: (property: number, rule: number, enabled: boolean, expectedRevision: number) =>
    send<{ id: number }>(`${rules(property)}/${rule}/enabled`, 'POST', { enabled, expectedRevision }),
  duplicateRule: (property: number, rule: number) => send<{ id: number }>(`${rules(property)}/${rule}/duplicate`, 'POST'),
  deleteRule: (property: number, rule: number, revision: number) => send<{ id: number }>(`${rules(property)}/${rule}${query({ revision })}`, 'DELETE'),
  incidents: (property: number, filters: IncidentFilters, signal?: AbortSignal) =>
    call<IncidentList>(`/properties/${property}/incidents${query(filters)}`, { signal }),
  incident: (property: number, incident: number, signal?: AbortSignal) =>
    call<IncidentDetail>(`/properties/${property}/incidents/${incident}`, { signal }),
  acknowledge: (property: number, incident: number) => send<{ id: number }>(`/properties/${property}/incidents/${incident}/acknowledge`, 'POST'),
  notifications: (filters: NotificationFilters, signal?: AbortSignal) => call<NotificationPage>(`/notifications${query(filters)}`, { signal }),
  summary: (signal?: AbortSignal) => call<NotificationSummary>('/notifications/summary', { signal }),
  markRead: (body: { ids?: number[]; all?: boolean; propertyId?: number }) => send<{ changed: number }>('/notifications/read', 'POST', body),
  reportDelivery: (notification: number, outcome: 'shown' | 'notShown', reason?: 'permission' | 'unsupported' | 'visible' | 'error') =>
    send<void>(`/notifications/${notification}/delivery`, 'POST', { channel: 'browser', outcome, reason: reason ?? null }),
  preferences: (signal?: AbortSignal) => call<NotificationPreferences>('/notifications/preferences', { signal }),
  savePreferences: (preferences: NotificationPreferences) => send<void>('/notifications/preferences', 'PUT', preferences),
}

export const severityLabel: Record<Severity, string> = { info: 'Info', warning: 'Warning', critical: 'Critical' }

export const operatorLabel: Record<ConditionOperator, string> = {
  equals: 'equals', notEquals: 'does not equal', lessThan: 'is below', lessThanOrEqual: 'is at or below', greaterThan: 'is above',
  greaterThanOrEqual: 'is at or above', isTrue: 'is on', isFalse: 'is off', eventOccurred: 'occurred', stateChanged: 'changed',
}

export const aggregationLabel: Record<Aggregation, string> = {
  single: 'the selected signal', any: 'any selected signal', all: 'every selected signal',
}

/**
 * The canonical signal identity, the same string the server's catalog sends as `key` (`AlertSignals.Key`). Metrics use their JSON
 * (camelCase) spelling. Labels are presentation only and never identify a signal.
 */
export const signalKey = (signal: SignalRef) => signal.kind === 'measurement'
  ? signal.installationId != null ? `m:i${signal.installationId}:${signal.metric ?? ''}`
    : `m:d${signal.deviceId ?? ''}:${signal.endpointKey ?? ''}:${signal.metric ?? ''}`
  : signal.kind === 'connectivity' ? `c:d${signal.deviceId ?? ''}` : `e:d${signal.deviceId ?? ''}:${signal.eventCode ?? ''}`

/**
 * Looks catalog entries up by the identity derived from each entry's own signal, so the editor, summaries, templates and the preview
 * resolve a stored SignalRef the same way. Entries with the same identity are one signal; equal labels never merge entries.
 */
export function signalIndex(catalog: SignalCatalogEntry[]) {
  const entries = new Map<string, SignalCatalogEntry>()
  for (const entry of catalog) if (!entries.has(signalKey(entry.signal))) entries.set(signalKey(entry.signal), entry)
  const labels = new Map([...entries].map(([key, entry]) => [key, entry.label]))
  return {
    entries: [...entries.values()], labels,
    get: (signal: SignalRef | undefined) => signal ? entries.get(signalKey(signal)) : undefined,
    byKey: (key: string) => entries.get(key),
  }
}
export type SignalIndex = ReturnType<typeof signalIndex>

export function conditions(group: RuleGroup): RuleCondition[] {
  return group.children.flatMap(node => node.condition ? [node.condition] : node.group ? conditions(node.group) : [])
}

const number = (value: number) => Number.isInteger(value) ? String(value) : value.toFixed(2).replace(/0+$/, '')
const unitText = (unit: string | null | undefined) => !unit ? '' : unit === '%' ? '%' : ` ${unit}`
const duration = (seconds: number) => seconds % 3600 === 0 && seconds >= 3600 ? `${seconds / 3600} h`
  : seconds % 60 === 0 && seconds >= 60 ? `${seconds / 60} min` : `${seconds} s`

export const unavailableSignal = 'an unavailable signal'

/** A readable sentence for one condition, using catalog labels and never raw identifiers. */
export function conditionText(condition: RuleCondition, labels: Map<string, string>) {
  const names = condition.signals.map(signal => labels.get(signalKey(signal)) ?? unavailableSignal)
  const subject = condition.signals.length > 1
    ? `${condition.aggregation === 'all' ? 'every one of' : 'any of'} ${names.join(', ')}` : names[0] ?? 'a signal'
  if (condition.operator === 'eventOccurred') return `${subject} in the last ${duration(condition.windowSeconds ?? 0)}`
  if (condition.operator === 'stateChanged') return `${subject} changed in the last ${duration(condition.windowSeconds ?? 0)}`
  if (condition.operator === 'isTrue' || condition.operator === 'isFalse') return `${subject} ${operatorLabel[condition.operator]}`
  const value = condition.number != null ? `${number(condition.number)}${unitText(condition.unit)}` : condition.text ?? ''
  return `${subject} ${operatorLabel[condition.operator]} ${value}`
}

export function groupText(group: RuleGroup, labels: Map<string, string>, nested = false): string {
  const parts = group.children.map(node => node.condition ? conditionText(node.condition, labels) : node.group ? groupText(node.group, labels, true) : '')
  const text = parts.join(group.operator === 'and' ? ' AND ' : ' OR ')
  return nested && parts.length > 1 ? `(${text})` : text
}

/** The editor's summary, for example: When … AND …, create or escalate a Critical grid outage incident. */
export function ruleSummary(input: Pick<AlertRuleInput, 'definition' | 'severity' | 'correlationKey' | 'incidentTitle' | 'name' | 'activationSeconds'>,
  labels: Map<string, string>) {
  const family = input.incidentTitle?.trim() || (input.correlationKey ? input.correlationKey.replace(/[.:_-]+/g, ' ') : input.name.trim() || 'this rule')
  const timing = input.activationSeconds > 0 ? ` for ${duration(input.activationSeconds)}` : ''
  return `When ${groupText(input.definition, labels)}${timing}, ${input.correlationKey ? 'create or escalate' : 'create'} a ${severityLabel[input.severity]} ${family.toLowerCase()} incident.`
}

export const formatDuration = duration

export function evidenceValue(signal: SignalEvidence) {
  if (signal.signal.kind === 'deviceEvent') return signal.events > 0 ? `${signal.events} event${signal.events === 1 ? '' : 's'}` : 'No events'
  if (signal.number != null) {
    const unit = signal.signal.metric ? metricUnits[signal.signal.metric] : null
    return `${number(signal.number)}${unitText(unit)}`
  }
  if (signal.flag != null) return signal.flag ? 'On' : 'Off'
  return signal.text ?? 'No value'
}

const metricUnits: Partial<Record<Metric, string>> = {
  pvPower: 'W', householdPower: 'W', batteryPower: 'W', activePower: 'W', gridImportPower: 'W', acChargingPower: 'W', acInputPower: 'W',
  voltage: 'V', gridVoltage: 'V', pvVoltage: 'V', pv1Voltage: 'V', acOutputVoltage: 'V', gridFrequency: 'Hz', current: 'A',
  energy: 'kWh', pvEnergyToday: 'kWh', outputEnergyToday: 'kWh', batteryDischargedEnergyToday: 'kWh', batterySoc: '%', humidity: '%',
  inverterLoadPercent: '%', temperature: '°C', targetTemperature: '°C', outdoorTemperature: '°C', leavingWaterTemperature: '°C',
  targetLeavingWaterTemperature: '°C', fanSpeed: 'step',
}

export const qualityLabel: Record<SignalEvidence['quality'], string> = {
  fresh: 'Fresh', stale: 'Too old', missing: 'No reading', invalid: 'Invalid', unavailable: 'Unavailable',
}

export const truthLabel: Record<Truth, string> = { true: 'True', false: 'False', unknown: 'Unknown' }

export const defaultPolicy: NotificationPolicy = {
  onActivation: true, onEscalation: true, onDeescalation: false, onResolution: true, reminderMinutes: 0, suppressWhileActive: null,
}
