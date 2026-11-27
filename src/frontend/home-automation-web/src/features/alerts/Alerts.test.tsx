import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import type { ReactNode } from 'react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, expect, it, vi } from 'vitest'
import {
  alertsApi, ruleSummary, type AlertRuleInput, type AlertRuleView, type IncidentDetail, type NotificationSummary, type SignalCatalogEntry,
} from '../../shared/alerts'
import { AlertRules } from './AlertRules'
import { AlertsPage } from './AlertsPage'
import { browserNotificationEvent, CriticalBanner, NotificationBell, useBrowserNotifications } from './NotificationCenter'
import { emptyRule, RuleEditor, validateRule } from './RuleEditor'

const property = { id: 1, name: 'Countryside House', timeZone: 'Europe/Sofia' }
const at = '2026-11-27T13:20:00Z'

const entry = (patch: Partial<SignalCatalogEntry> & Pick<SignalCatalogEntry, 'key' | 'label' | 'signal'>): SignalCatalogEntry => ({
  deviceId: 1, deviceName: 'Inverter A', endpointName: null, provider: 'growatt', valueType: 'number', unit: 'V',
  operators: ['lessThan', 'lessThanOrEqual', 'greaterThan', 'greaterThanOrEqual', 'equals', 'notEquals'], states: [], samplingSeconds: 300,
  suggestedFreshnessSeconds: 900, number: 0, flag: null, text: null, observedAt: at, ...patch,
})
const voltage = entry({ key: 'm:d1:ac:gridVoltage', label: 'Inverter A · AC input voltage',
  signal: { kind: 'measurement', deviceId: 1, endpointKey: 'ac', metric: 'gridVoltage' } })
const soc = entry({ key: 'm:d3:battery:batterySoc', label: 'Battery · Battery SOC', unit: '%', deviceId: 3,
  signal: { kind: 'measurement', deviceId: 3, endpointKey: 'battery', metric: 'batterySoc' } })
const relay = entry({ key: 'm:d5:relay:switchOn', label: 'Heater · Relay', unit: null, valueType: 'boolean', deviceId: 5,
  operators: ['isTrue', 'isFalse', 'stateChanged'], signal: { kind: 'measurement', deviceId: 5, endpointKey: 'relay', metric: 'switchOn' } })
const catalog = [voltage, soc, relay]
const labels = new Map(catalog.map(item => [item.key, item.label]))

const gridLowBattery: AlertRuleInput = {
  name: 'Grid outage with low battery', enabled: true, severity: 'critical', correlationKey: 'grid.outage', incidentTitle: 'Grid outage',
  definition: { operator: 'and', children: [
    { condition: { id: 'c1', signals: [voltage.signal], aggregation: 'single', operator: 'lessThan', number: 10, unit: 'V', freshnessSeconds: 900 } },
    { condition: { id: 'c2', signals: [soc.signal], aggregation: 'single', operator: 'lessThan', number: 50, unit: '%', recoveryNumber: 55,
      freshnessSeconds: 900 } },
  ] },
  activationSeconds: 0, recoverySeconds: 120,
}

function show(node: ReactNode, path = '/properties/1/automations') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[path]}>
    <Routes><Route path="*" element={node} /></Routes></MemoryRouter></QueryClientProvider>)
}

afterEach(() => vi.restoreAllMocks())

it('summarizes a composite rule in plain words and validates before saving', () => {
  expect(ruleSummary(gridLowBattery, labels)).toBe(
    'When Inverter A · AC input voltage is below 10 V AND Battery · Battery SOC is below 50%, create or escalate a Critical grid outage incident.')
  expect(validateRule(emptyRule())).toEqual(['Give the rule a name.', 'Add at least one condition.'])
  const wrongSide = structuredClone(gridLowBattery)
  wrongSide.definition.children[1].condition!.recoveryNumber = 45
  expect(validateRule(wrongSide)).toContain('Condition 2: the recovery threshold must be on the recovered side of the activation threshold.')
  expect(validateRule({ ...gridLowBattery, correlationKey: 'Grid Outage' })[0]).toMatch(/correlation group/)
})

it('builds a rule visually, offers only valid comparisons per signal type, previews without saving and saves', async () => {
  const create = vi.spyOn(alertsApi, 'createRule').mockResolvedValue({ id: 9 })
  const preview = vi.spyOn(alertsApi, 'preview').mockResolvedValue({ valid: true, errors: [], result: 'unknown', evaluatedAt: at, conditions: [
    { conditionId: 'c1', result: 'unknown', signals: [{ signal: soc.signal, label: soc.label, result: 'unknown', quality: 'stale', number: 45, flag: null,
      text: null, observedAt: '2026-11-26T13:20:00Z', events: 0, reason: 'The latest reading is older than the allowed age.' }] },
  ] })
  const saved = vi.fn()
  show(<RuleEditor property={property} catalog={catalog} initial={emptyRule()} onSaved={saved} onCancel={() => undefined} />)
  fireEvent.click(screen.getByRole('button', { name: 'Save rule' }))
  expect(await screen.findByRole('alert')).toHaveTextContent('Give the rule a name.')
  expect(create).not.toHaveBeenCalled()

  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Battery low' } })
  fireEvent.click(screen.getByRole('button', { name: 'Add condition' }))
  const condition = within(screen.getByRole('group', { name: 'Condition c1' }))
  fireEvent.change(condition.getByLabelText('Signal'), { target: { value: relay.key } })
  expect(condition.getByLabelText('Comparison')).toHaveTextContent('is on')
  expect(within(condition.getByLabelText('Comparison')).queryByRole('option', { name: 'is below' })).not.toBeInTheDocument()
  expect(condition.queryByLabelText(/Threshold/)).not.toBeInTheDocument()

  fireEvent.change(condition.getByLabelText('Signal'), { target: { value: soc.key } })
  fireEvent.change(condition.getByLabelText('Comparison'), { target: { value: 'lessThan' } })
  fireEvent.change(condition.getByLabelText('Threshold (%)'), { target: { value: '35' } })
  fireEvent.change(condition.getByLabelText('Recovers above (%)'), { target: { value: '40' } })
  expect(screen.getByText(/When Battery · Battery SOC is below 35% for 1 min, create a Warning battery low incident\./)).toBeInTheDocument()

  fireEvent.click(screen.getByRole('button', { name: 'Preview with latest readings' }))
  const table = await screen.findByRole('region', { name: 'Rule preview' })
  expect(within(table).getByText('Too old · The latest reading is older than the allowed age.')).toBeInTheDocument()
  expect(within(table).getAllByText('Unknown').length).toBeGreaterThan(0)
  expect(within(table).getByText(/never creates incidents or notifications/)).toBeInTheDocument()
  expect(preview).toHaveBeenCalledTimes(1)
  expect(create).not.toHaveBeenCalled()

  fireEvent.click(screen.getByRole('button', { name: 'Save rule' }))
  await waitFor(() => expect(create).toHaveBeenCalledTimes(1))
  expect(create.mock.calls[0][1].definition).toEqual({ operator: 'and', children: [{ condition: expect.objectContaining({
    id: 'c1', signals: [soc.signal], operator: 'lessThan', number: 35, recoveryNumber: 40, unit: '%', freshnessSeconds: 900 }) }] })
  expect(saved).toHaveBeenCalledWith('Rule “Battery low” created.')
})

it('nests a group and lets several signals be combined with every or any', () => {
  const second = entry({ key: 'm:d2:ac:gridVoltage', label: 'Inverter B · AC input voltage', deviceId: 2,
    signal: { kind: 'measurement', deviceId: 2, endpointKey: 'ac', metric: 'gridVoltage' } })
  show(<RuleEditor property={property} catalog={[voltage, second, soc]} initial={emptyRule()} onSaved={() => undefined} onCancel={() => undefined} />)
  fireEvent.click(screen.getByRole('button', { name: 'Add condition' }))
  fireEvent.click(screen.getByLabelText('Inverter B · AC input voltage'))
  expect(screen.getByLabelText('Combine')).toHaveValue('all')
  fireEvent.click(screen.getAllByRole('button', { name: 'Add group' })[0])
  expect(screen.getByRole('group', { name: 'Condition group' })).toBeInTheDocument()
  expect(screen.getByText(/every one of Inverter A · AC input voltage, Inverter B · AC input voltage is below 0 V AND Inverter A · AC input voltage is below 0 V /)).toBeInTheDocument()
})

const rule = (patch: Partial<AlertRuleView>): AlertRuleView => ({
  ...gridLowBattery, id: 4, propertyId: 1, notifications: { onActivation: true, onEscalation: true, onDeescalation: false, onResolution: true,
    reminderMinutes: 0 }, revision: 3, createdAt: at, updatedAt: at, warnings: [],
  status: { phase: 'active', result: 'true', lastEvaluatedAt: at, lastTriggeredAt: at, incidentId: 12, incidentSeverity: 'critical', incidentStatus: 'active' },
  ...patch,
})

function rules(canManage: boolean, list: AlertRuleView[]) {
  vi.spyOn(alertsApi, 'rules').mockResolvedValue({ canManage, rules: list })
  vi.spyOn(alertsApi, 'catalog').mockResolvedValue(catalog)
  vi.spyOn(alertsApi, 'templates').mockResolvedValue({ templates: [
    { code: 'battery-low', name: 'Battery low', description: 'Warning below 35%.', available: true, unavailable: null, draft: { ...gridLowBattery, name: 'Battery low' } },
    { code: 'high-temperature', name: 'High room temperature', description: 'Warning above a threshold.', available: false,
      unavailable: 'No device in this property reports temperature.', draft: null },
  ], gridOutageSetup: [gridLowBattery, { ...gridLowBattery, name: 'Grid power lost', severity: 'warning' }] })
  show(<AlertRules property={property} properties={[property, { id: 2, name: 'Lake House', timeZone: 'UTC' }]} />)
}

it('lists rules with severity, summary, last trigger and incident, and changes them only after confirmation', async () => {
  rules(true, [rule({}), rule({ id: 5, name: 'Battery low', severity: 'warning', enabled: false, correlationKey: null,
    status: { phase: 'inactive', result: 'false', lastEvaluatedAt: at, lastTriggeredAt: null, incidentId: null, incidentSeverity: null, incidentStatus: null },
    warnings: ['A referenced device moved to another property or was removed. Its conditions evaluate as unknown.'] })])
  const toggle = vi.spyOn(alertsApi, 'setEnabled').mockResolvedValue({ id: 4 })
  const remove = vi.spyOn(alertsApi, 'deleteRule').mockResolvedValue({ id: 4 })
  const table = await screen.findByRole('table')
  const row = within(table).getByText('Grid outage with low battery').closest('tr')!
  expect(within(row).getAllByText('Critical').length).toBeGreaterThan(0)
  expect(await within(row).findByText(/Inverter A · AC input voltage is below 10 V AND Battery · Battery SOC is below 50%/)).toBeInTheDocument()
  expect(within(row).getByText('Active')).toBeInTheDocument()
  expect(within(table).getByText(/moved to another property/)).toBeInTheDocument()
  expect(within(table).getByText('Never')).toBeInTheDocument()

  fireEvent.click(within(row).getByRole('switch', { name: 'Grid outage with low battery enabled' }))
  await waitFor(() => expect(toggle).toHaveBeenCalledWith(1, 4, false, 3))

  fireEvent.change(screen.getByLabelText('Severity'), { target: { value: 'warning' } })
  expect(within(screen.getByRole('table')).queryByText('Grid outage with low battery')).not.toBeInTheDocument()
  fireEvent.change(screen.getByLabelText('Severity'), { target: { value: '' } })
  fireEvent.change(screen.getByLabelText('Status'), { target: { value: 'disabled' } })
  expect(within(screen.getByRole('table')).getByText('Battery low')).toBeInTheDocument()
  fireEvent.change(screen.getByLabelText('Status'), { target: { value: '' } })

  fireEvent.click(screen.getByRole('button', { name: 'Actions for Grid outage with low battery' }))
  fireEvent.click(screen.getByRole('menuitem', { name: 'Delete…' }))
  const dialog = within(screen.getByRole('dialog'))
  expect(dialog.getByText(/Past incidents, their timelines and notifications are kept/)).toBeInTheDocument()
  expect(remove).not.toHaveBeenCalled()
  fireEvent.click(dialog.getByRole('button', { name: 'Delete rule' }))
  await waitFor(() => expect(remove).toHaveBeenCalledWith(1, 4, 3))
})

it('offers templates only for available signals and sets up grid outage alerts through an explicit confirmation', async () => {
  rules(true, [])
  const create = vi.spyOn(alertsApi, 'createRules').mockResolvedValue({ id: 7 })
  fireEvent.click(await screen.findByRole('button', { name: 'Templates' }))
  expect(await screen.findByText('No device in this property reports temperature.')).toBeInTheDocument()
  fireEvent.click(await screen.findByRole('button', { name: 'Set up grid outage alerts' }))
  const dialog = within(screen.getByRole('dialog'))
  expect(dialog.getByText(/opens as Warning and escalates to Critical/)).toBeInTheDocument()
  expect(create).not.toHaveBeenCalled()
  fireEvent.click(dialog.getByRole('button', { name: 'Create and enable both rules' }))
  await waitFor(() => expect(create).toHaveBeenCalledWith(1, [gridLowBattery, expect.objectContaining({ name: 'Grid power lost' })]))

  // The templates panel is still open; a template opens the editor disabled until the user enables and saves it.
  fireEvent.click(await screen.findByRole('button', { name: 'Review and use' }))
  expect(await screen.findByLabelText('Name')).toHaveValue('Battery low')
  expect(screen.getByLabelText('Enabled')).not.toBeChecked()
})

it('shows viewers rules read-only', async () => {
  rules(false, [rule({})])
  expect(await screen.findByText(/Owners, Admins and Operators can change them/)).toBeInTheDocument()
  expect(screen.queryByRole('switch')).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'New rule' })).not.toBeInTheDocument()
})

const detail: IncidentDetail = {
  canAcknowledge: true,
  incident: { id: 12, propertyId: 1, propertyName: 'Countryside House', title: 'Grid outage', correlationKey: 'grid.outage', status: 'active',
    severity: 'critical', originalSeverity: 'warning', maxSeverity: 'critical', startedAt: '2026-11-27T13:21:00Z', lastChangedAt: at,
    resolvedAt: null, resolution: null, acknowledgedAt: null, rules: [
      { ruleId: 3, ruleName: 'Grid power lost', revision: 1, severity: 'warning', active: true, firstActiveAt: at, lastActiveAt: at, clearedAt: null,
        clearedReason: null },
      { ruleId: 4, ruleName: 'Grid outage with low battery', revision: 2, severity: 'critical', active: true, firstActiveAt: at, lastActiveAt: at,
        clearedAt: null, clearedReason: null }] },
  timeline: [
    { id: 1, occurredAt: '2026-11-27T13:21:00Z', observedAt: '2026-11-27T13:20:00Z', kind: 'opened', ruleId: 3, ruleRevision: 1, fromSeverity: null,
      toSeverity: 'warning', detail: { ruleName: 'Grid power lost', evidence: [{ conditionId: 'c1', result: 'true', signals: [{ signal: voltage.signal,
        label: 'Inverter A · AC input voltage', result: 'true', quality: 'fresh', number: 0, flag: null, text: null, observedAt: '2026-11-27T13:20:00Z',
        events: 0, reason: null }] }] } },
    { id: 2, occurredAt: '2026-11-27T13:46:00Z', observedAt: '2026-11-27T13:46:00Z', kind: 'escalated', ruleId: 4, ruleRevision: 2,
      fromSeverity: 'warning', toSeverity: 'critical', detail: { ruleName: 'Grid outage with low battery', evidence: [{ conditionId: 'c2', result: 'true',
        signals: [{ signal: soc.signal, label: 'Battery · Battery SOC', result: 'true', quality: 'fresh', number: 49, flag: null, text: null,
          observedAt: '2026-11-27T13:46:00Z', events: 0, reason: null }] }] } },
  ],
}

it('shows an incident timeline with evidence and acknowledges without resolving', async () => {
  vi.spyOn(alertsApi, 'incidents').mockResolvedValue({ canAcknowledge: true, incidents: [detail.incident] })
  vi.spyOn(alertsApi, 'incident').mockResolvedValue(detail)
  const acknowledge = vi.spyOn(alertsApi, 'acknowledge').mockResolvedValue({ id: 12 })
  show(<AlertsPage property={property} properties={[property]} />, '/properties/1/alerts?incident=12')
  const panel = await screen.findByRole('heading', { name: 'Grid outage', level: 2 })
  const card = panel.closest('section')!
  expect(within(card).getByText(/Warning incident opened by Grid power lost/)).toBeInTheDocument()
  expect(within(card).getByText(/Escalated from Warning to Critical by Grid outage with low battery/)).toBeInTheDocument()
  expect(within(card).getByText(/Battery · Battery SOC: 49%/)).toBeInTheDocument()
  expect(within(card).getByText(/started as Warning · highest Critical/)).toBeInTheDocument()
  fireEvent.click(within(card).getByRole('button', { name: 'Acknowledge' }))
  await waitFor(() => expect(acknowledge).toHaveBeenCalledWith(1, 12))
})

it('keeps Critical in-app notifications on and saves notification settings', async () => {
  vi.spyOn(alertsApi, 'incidents').mockResolvedValue({ canAcknowledge: false, incidents: [] })
  vi.spyOn(alertsApi, 'preferences').mockResolvedValue({ info: { inApp: true, browser: false }, warning: { inApp: true, browser: true },
    critical: { inApp: true, browser: true, sound: false }, events: { activated: true, escalated: true, deescalated: true, resolved: true, reminder: true } })
  const save = vi.spyOn(alertsApi, 'savePreferences').mockResolvedValue(undefined)
  show(<AlertsPage property={property} properties={[property]} />, '/properties/1/alerts')
  fireEvent.click(screen.getByRole('tab', { name: 'Notification settings' }))
  const critical = await screen.findByRole('checkbox', { name: 'Critical in the app' })
  expect(critical).toBeChecked()
  expect(critical).toBeDisabled()
  fireEvent.click(screen.getByRole('checkbox', { name: 'Warning browser notification' }))
  fireEvent.click(screen.getByRole('checkbox', { name: 'Critical sound' }))
  fireEvent.click(screen.getByRole('button', { name: 'Save settings' }))
  await waitFor(() => expect(save).toHaveBeenCalledWith(expect.objectContaining({
    warning: { inApp: true, browser: false }, critical: { inApp: true, browser: true, sound: true } })))
})

it('shows unread counts in the bell and keeps an active Critical incident visible after everything was read', async () => {
  const summary: NotificationSummary = { unread: 0, unreadCritical: 0, unreadWarning: 0, activeCritical: [detail.incident] }
  vi.spyOn(alertsApi, 'summary').mockResolvedValue({ ...summary, unread: 3, unreadCritical: 1 })
  vi.spyOn(alertsApi, 'notifications').mockResolvedValue({ next: null, items: [] })
  const read = vi.spyOn(alertsApi, 'markRead').mockResolvedValue({ changed: 3 })
  show(<><NotificationBell property={property} /><CriticalBanner property={property} /></>, '/properties/1')
  const bell = await screen.findByRole('button', { name: 'Notifications, 3 unread, 1 critical' })
  expect(screen.getByRole('alert')).toHaveTextContent('Grid outage')
  vi.mocked(alertsApi.summary).mockResolvedValue(summary)
  fireEvent.click(bell)
  fireEvent.click(await screen.findByRole('button', { name: 'Mark all read' }))
  await waitFor(() => expect(read).toHaveBeenCalledWith({ all: true }))
  expect(await screen.findByRole('button', { name: 'Notifications, 0 unread' })).toBeInTheDocument()
  expect(screen.getByRole('alert')).toHaveTextContent('Critical')
  expect(screen.getByRole('alert')).toHaveTextContent('Grid outage')
})

function Browser() {
  useBrowserNotifications()
  return null
}

it('shows a system notification only for a background tab with permission and reports what happened', async () => {
  const report = vi.spyOn(alertsApi, 'reportDelivery').mockResolvedValue(undefined)
  const created: { title: string; options?: NotificationOptions }[] = []
  class FakeNotification {
    static permission: NotificationPermission = 'granted'
    onclick: (() => void) | null = null
    constructor(title: string, options?: NotificationOptions) { created.push({ title, options }) }
  }
  vi.stubGlobal('Notification', FakeNotification)
  const visibility = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden')
  render(<Browser />)
  const send = (id: number) => act(() => { window.dispatchEvent(new CustomEvent(browserNotificationEvent, { detail: {
    notificationId: id, propertyId: 1, incidentId: 12, kind: 'escalated', severity: 'critical', title: 'Grid outage escalated to Critical',
    body: 'Battery SOC 49%.', createdAt: at, sound: false } })) })
  send(1)
  expect(created).toEqual([{ title: 'Grid outage escalated to Critical', options: expect.objectContaining({ body: 'Battery SOC 49%.',
    tag: 'homeautomation-1', requireInteraction: true }) }])
  expect(report).toHaveBeenLastCalledWith(1, 'shown', undefined)
  visibility.mockReturnValue('visible')
  send(2)
  expect(report).toHaveBeenLastCalledWith(2, 'notShown', 'visible')
  visibility.mockReturnValue('hidden')
  FakeNotification.permission = 'denied'
  send(3)
  expect(report).toHaveBeenLastCalledWith(3, 'notShown', 'permission')
  expect(created).toHaveLength(1)
  vi.unstubAllGlobals()
})
