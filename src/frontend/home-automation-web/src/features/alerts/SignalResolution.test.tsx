// Signal identity in the rule editor, templates, the grid outage setup and the preview (prompt 0066).
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import type { ReactNode } from 'react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, expect, it, vi } from 'vitest'
import {
  alertsApi, signalIndex, signalKey, type AlertRuleInput, type AlertRuleView, type SignalCatalogEntry, type SignalRef,
} from '../../shared/alerts'
import { AlertRules } from './AlertRules'
import { RuleEditor } from './RuleEditor'

const house = { id: 1, name: 'Countryside House', timeZone: 'Europe/Sofia' }
const lake = { id: 2, name: 'Lake House', timeZone: 'UTC' }
const at = '2026-11-27T13:20:00Z'
const numberOperators = ['lessThan', 'lessThanOrEqual', 'greaterThan', 'greaterThanOrEqual', 'equals', 'notEquals'] as const

// The server writes every SignalRef field, with nulls, and sends AlertSignals.Key as `key`.
const ref = (patch: Partial<SignalRef> & Pick<SignalRef, 'kind'>): SignalRef =>
  ({ deviceId: null, endpointKey: null, installationId: null, metric: null, eventCode: null, ...patch })
const entry = (patch: Partial<SignalCatalogEntry> & Pick<SignalCatalogEntry, 'label' | 'signal'>): SignalCatalogEntry => ({
  key: signalKey(patch.signal), deviceId: patch.signal.deviceId ?? null, deviceName: null, endpointName: null, provider: 'growatt',
  valueType: 'number', unit: 'V', operators: [...numberOperators], states: [], samplingSeconds: 300, suggestedFreshnessSeconds: 900,
  number: null, flag: null, text: null, observedAt: at, ...patch,
})
const soc = entry({ label: 'Growatt shared battery · Main · Battery SOC', unit: '%', number: 38, deviceName: 'Growatt shared battery',
  endpointName: 'Main', signal: ref({ kind: 'measurement', deviceId: 21, endpointKey: 'main', metric: 'batterySoc' }) })
const voltage = entry({ label: 'Growatt Master · AC input voltage', number: 244.5, deviceName: 'Growatt Master',
  signal: ref({ kind: 'measurement', deviceId: 22, endpointKey: 'ac', metric: 'gridVoltage' }) })
const slaveVoltage = entry({ label: 'Growatt Slave · AC input voltage', number: 243.9, deviceName: 'Growatt Slave',
  signal: ref({ kind: 'measurement', deviceId: 23, endpointKey: 'ac', metric: 'gridVoltage' }) })
const event = (device: number, label: string, code = 'DEVICE_OFFLINE') => entry({
  label, provider: 'daikin', valueType: 'event', unit: null, operators: ['eventOccurred'], observedAt: null,
  signal: ref({ kind: 'deviceEvent', deviceId: device, eventCode: code }),
})
const altherma = event(31, 'Heatpump (Altherma) went offline')
const heatpumpSwitch = event(32, 'Heatpump (PSF-X67) went offline')
const catalog = [soc, voltage, slaveVoltage, altherma, heatpumpSwitch]

const batteryLow: AlertRuleInput = {
  name: 'Battery low', description: null, enabled: true, severity: 'warning', correlationKey: 'battery.low', incidentTitle: 'Battery low',
  definition: { operator: 'and', children: [{ condition: {
    id: 'c1', signals: [soc.signal], aggregation: 'single', operator: 'lessThan', number: 35, unit: '%', recoveryNumber: 40, freshnessSeconds: 900,
  } }] },
  activationSeconds: 120, recoverySeconds: 300,
}
// The grid setup comes from the server without null fields, as stored definitions are written.
const lean = (signal: SignalRef): SignalRef => Object.fromEntries(Object.entries(signal).filter(([, value]) => value != null)) as SignalRef
const gridSetup: AlertRuleInput[] = [
  { ...batteryLow, name: 'Grid power lost', severity: 'warning', correlationKey: 'grid.outage', incidentTitle: 'Grid outage',
    definition: { operator: 'and', children: [{ condition: { id: 'c1', signals: [lean(voltage.signal), lean(slaveVoltage.signal)], aggregation: 'all',
      operator: 'lessThanOrEqual', number: 10, unit: 'V', freshnessSeconds: 900 } }] } },
  { ...batteryLow, name: 'Grid outage with low battery', severity: 'critical', correlationKey: 'grid.outage', incidentTitle: 'Grid outage',
    definition: { operator: 'and', children: [
      { condition: { id: 'c1', signals: [lean(voltage.signal), lean(slaveVoltage.signal)], aggregation: 'all', operator: 'lessThanOrEqual',
        number: 10, unit: 'V', freshnessSeconds: 900 } },
      { condition: { id: 'c2', signals: [lean(soc.signal)], aggregation: 'single', operator: 'lessThan', number: 50, unit: '%', recoveryNumber: 55,
        freshnessSeconds: 900 } },
    ] } },
]

function show(node: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/properties/1/automations']}>
    <Routes><Route path="*" element={node} /></Routes></MemoryRouter></QueryClientProvider>)
}

function mockApi(rules: AlertRuleView[] = [], catalogs: Record<number, SignalCatalogEntry[]> = { 1: catalog }) {
  vi.spyOn(alertsApi, 'rules').mockResolvedValue({ canManage: true, rules })
  const catalogCall = vi.spyOn(alertsApi, 'catalog').mockImplementation(async property => catalogs[property] ?? [])
  vi.spyOn(alertsApi, 'templates').mockResolvedValue({ templates: [
    { code: 'battery-low', name: 'Battery low', description: 'Warning below 35%.', available: true, unavailable: null, draft: batteryLow },
  ], gridOutageSetup: gridSetup })
  return catalogCall
}

async function newCondition() {
  const button = await screen.findByRole('button', { name: 'New rule' })
  await waitFor(() => expect(button).toBeEnabled())
  fireEvent.click(button)
  fireEvent.click(await screen.findByRole('button', { name: 'Add condition' }))
  return within(screen.getByRole('group', { name: 'Condition c1' }))
}

afterEach(() => vi.restoreAllMocks())

it('keeps the Growatt shared battery SOC selected with its comparisons, % threshold and latest reading', async () => {
  mockApi()
  show(<AlertRules property={house} properties={[house]} />)
  const condition = await newCondition()
  const signal = condition.getByRole('combobox', { name: 'Signal' })
  fireEvent.change(signal, { target: { value: soc.key } })
  expect(signal).toHaveValue(soc.key)
  expect(within(signal).getByRole('option', { selected: true })).toHaveTextContent('Growatt shared battery · Main · Battery SOC')
  expect(condition.queryByText(/no longer/i)).not.toBeInTheDocument()
  expect(condition.queryByRole('option', { name: /Unavailable signal/ })).not.toBeInTheDocument()
  expect(within(condition.getByLabelText('Comparison')).getAllByRole('option').map(option => option.textContent))
    .toEqual(['is below', 'is at or below', 'is above', 'is at or above', 'equals', 'does not equal'])
  expect(condition.getByLabelText('Threshold (%)')).toBeInTheDocument()
  expect(condition.getByText(/Latest reading 38%, observed/)).toBeInTheDocument()
})

it('keeps the Growatt Master AC input voltage selected and its threshold visible through later edits', async () => {
  mockApi()
  show(<AlertRules property={house} properties={[house]} />)
  const condition = await newCondition()
  const signal = condition.getByRole('combobox', { name: 'Signal' })
  fireEvent.change(signal, { target: { value: voltage.key } })
  fireEvent.change(condition.getByLabelText('Comparison'), { target: { value: 'lessThanOrEqual' } })
  fireEvent.change(condition.getByLabelText('Threshold (V)'), { target: { value: '180' } })
  fireEvent.change(condition.getByLabelText('Recovers above (V)'), { target: { value: '200' } })
  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Low grid voltage' } })
  fireEvent.change(condition.getByLabelText('Ignore readings older than (minutes)'), { target: { value: '20' } })
  expect(signal).toHaveValue(voltage.key)
  expect(condition.getByLabelText('Threshold (V)')).toHaveValue(180)
  expect(condition.getByText(/Latest reading 244.5 V/)).toBeInTheDocument()
  expect(screen.getByText(/When Growatt Master · AC input voltage is at or below 180 V for 1 min/)).toBeInTheDocument()
  // Another Growatt meter of the same metric can be added; both stay checked by identity.
  fireEvent.click(condition.getByRole('checkbox', { name: 'Growatt Slave · AC input voltage' }))
  expect(condition.getByRole('checkbox', { name: 'Growatt Slave · AC input voltage' })).toBeChecked()
  expect(signal).toHaveValue(voltage.key)
})

it('initializes the Battery low template with its signal selected and its threshold filled in', async () => {
  mockApi()
  show(<AlertRules property={house} properties={[house]} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Templates' }))
  fireEvent.click(await screen.findByRole('button', { name: 'Review and use' }))
  const condition = within(await screen.findByRole('group', { name: 'Condition c1' }))
  expect(condition.getByRole('combobox', { name: 'Signal' })).toHaveValue(soc.key)
  expect(condition.getByLabelText('Threshold (%)')).toHaveValue(35)
  expect(condition.getByLabelText('Recovers above (%)')).toHaveValue(40)
  expect(screen.getByLabelText('Enabled')).not.toBeChecked()
  expect(screen.getByText(/When Growatt shared battery · Main · Battery SOC is below 35% for 2 min/)).toBeInTheDocument()
})

it('describes the grid outage setup with device names, comparisons and thresholds, and creates nothing until confirmed', async () => {
  mockApi()
  const create = vi.spyOn(alertsApi, 'createRules').mockResolvedValue({ id: 7 })
  show(<AlertRules property={house} properties={[house]} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Set up grid outage alerts' }))
  const dialog = await screen.findByRole('dialog')
  await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Create and enable both rules' })).toBeEnabled())
  expect(dialog).toHaveTextContent('Grid power lost: When every one of Growatt Master · AC input voltage, Growatt Slave · AC input voltage is at or below 10 V')
  expect(dialog).toHaveTextContent(
    'AND Growatt shared battery · Main · Battery SOC is below 50% for 2 min, create or escalate a Critical grid outage incident.')
  expect(dialog).not.toHaveTextContent('unavailable')
  fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
  expect(create).not.toHaveBeenCalled()
})

it('keeps the grid outage confirmation disabled until signal names have loaded', async () => {
  vi.spyOn(alertsApi, 'rules').mockResolvedValue({ canManage: true, rules: [] })
  vi.spyOn(alertsApi, 'catalog').mockReturnValue(new Promise(() => undefined))
  vi.spyOn(alertsApi, 'templates').mockResolvedValue({ templates: [], gridOutageSetup: gridSetup })
  show(<AlertRules property={house} properties={[house]} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Set up grid outage alerts' }))
  const dialog = await screen.findByRole('dialog')
  expect(within(dialog).getByRole('status')).toHaveTextContent('Loading signal names…')
  expect(within(dialog).getByRole('button', { name: 'Create and enable both rules' })).toBeDisabled()
})

it('offers distinct signals with equal names separately and never merges them by label', async () => {
  const twin = event(33, 'Heatpump (PSF-X67) went offline')
  const index = signalIndex([...catalog, twin, { ...altherma }])
  expect(index.entries).toHaveLength(catalog.length + 1)
  mockApi([], { 1: [...catalog, twin] })
  show(<AlertRules property={house} properties={[house]} />)
  const condition = await newCondition()
  const signal = condition.getByRole('combobox', { name: 'Signal' })
  expect(within(signal).getAllByRole('option', { name: 'Heatpump (PSF-X67) went offline' })).toHaveLength(2)
  expect(within(signal).getByRole('option', { name: 'Heatpump (Altherma) went offline' })).toBeInTheDocument()
  fireEvent.change(signal, { target: { value: twin.key } })
  expect(signal).toHaveValue(twin.key)
  expect(condition.getByLabelText('Comparison')).toHaveTextContent('occurred')
})

it('saves exactly the catalog signal identity and keeps a stored rule unchanged when it is opened and saved', async () => {
  const create = vi.spyOn(alertsApi, 'createRule').mockResolvedValue({ id: 3 })
  show(<RuleEditor property={house} catalog={catalog} initial={{ ...batteryLow, definition: { operator: 'and', children: [] } }}
    onSaved={() => undefined} onCancel={() => undefined} />)
  fireEvent.click(screen.getByRole('button', { name: 'Add condition' }))
  const condition = within(screen.getByRole('group', { name: 'Condition c1' }))
  fireEvent.change(condition.getByRole('combobox', { name: 'Signal' }), { target: { value: voltage.key } })
  fireEvent.change(condition.getByLabelText('Threshold (V)'), { target: { value: '180' } })
  fireEvent.click(screen.getByRole('button', { name: 'Save rule' }))
  await waitFor(() => expect(create).toHaveBeenCalledTimes(1))
  const saved = create.mock.calls[0][1].definition.children[0].condition!
  expect(saved.signals).toEqual([voltage.signal])
  expect(signalKey(saved.signals[0])).toBe('m:d22:ac:gridVoltage')
  expect(saved.unit).toBe('V')
})

it('resolves stored rules regardless of null fields or the older catalog key spelling', async () => {
  // Rules stored before this fix omit nulls; catalogs from commit a2bf0b4 spelled metrics in PascalCase inside `key`.
  const legacy = catalog.map(item => item.signal.metric
    ? { ...item, key: item.key.replace(/:([a-z])(\w*)$/, (_, first: string, rest: string) => `:${first.toUpperCase()}${rest}`) } : item)
  expect(legacy[0].key).toBe('m:d21:main:BatterySoc')
  const update = vi.spyOn(alertsApi, 'updateRule').mockResolvedValue({ id: 4 })
  const stored = { ...gridSetup[1] }
  show(<RuleEditor property={house} catalog={legacy} initial={stored} ruleId={4} revision={2} onSaved={() => undefined} onCancel={() => undefined} />)
  const battery = within(screen.getByRole('group', { name: 'Condition c2' }))
  expect(battery.getByRole('combobox', { name: 'Signal' })).toHaveDisplayValue('Growatt shared battery · Main · Battery SOC')
  expect(battery.getByLabelText('Threshold (%)')).toHaveValue(50)
  const grid = within(screen.getByRole('group', { name: 'Condition c1' }))
  expect(grid.getByRole('checkbox', { name: 'Growatt Slave · AC input voltage' })).toBeChecked()
  fireEvent.click(screen.getByRole('button', { name: 'Save rule' }))
  await waitFor(() => expect(update).toHaveBeenCalledTimes(1))
  expect(update.mock.calls[0][2].definition).toEqual(stored.definition)
})

it('loads the signals of the property chosen in the list, and selections stay on that property', async () => {
  const otherSoc = entry({ label: 'Lake battery · Battery SOC', unit: '%', number: 71,
    signal: ref({ kind: 'measurement', deviceId: 41, endpointKey: 'battery', metric: 'batterySoc' }) })
  const catalogCall = mockApi([], { 1: catalog, 2: [otherSoc] })
  show(<AlertRules property={house} properties={[house, lake]} />)
  fireEvent.change(await screen.findByLabelText('Property'), { target: { value: '2' } })
  await waitFor(() => expect(catalogCall).toHaveBeenCalledWith(2, expect.anything()))
  const condition = await newCondition()
  const signal = condition.getByRole('combobox', { name: 'Signal' })
  expect(within(signal).getAllByRole('option').map(option => option.textContent)).toEqual(['Lake battery · Battery SOC'])
  expect(signal).toHaveValue(otherSoc.key)
  expect(condition.getByLabelText('Threshold (%)')).toBeInTheDocument()
})

it('names preview signals exactly as the editor does and keeps stale readings distinct from unavailable signals', async () => {
  vi.spyOn(alertsApi, 'preview').mockResolvedValue({ valid: true, errors: [], result: 'unknown', evaluatedAt: at, conditions: [
    { conditionId: 'c1', result: 'unknown', signals: [{ signal: lean(soc.signal), label: 'Growatt shared battery · Battery SOC', result: 'unknown',
      quality: 'stale', number: 38, flag: null, text: null, observedAt: '2026-11-26T13:20:00Z', events: 0,
      reason: 'The latest reading is older than the allowed age.' }] },
  ] })
  show(<RuleEditor property={house} catalog={catalog} initial={batteryLow} onSaved={() => undefined} onCancel={() => undefined} />)
  const condition = within(screen.getByRole('group', { name: 'Condition c1' }))
  expect(condition.getByRole('combobox', { name: 'Signal' })).toHaveDisplayValue(soc.label)
  fireEvent.click(screen.getByRole('button', { name: 'Preview with latest readings' }))
  const table = within(await screen.findByRole('region', { name: 'Rule preview' }))
  expect(table.getByText(soc.label)).toBeInTheDocument()
  expect(table.getByText(/^Too old/)).toBeInTheDocument()
  expect(condition.queryByText(/no longer offers/)).not.toBeInTheDocument()
})

it('keeps an unavailable stored signal as it is, explains why and never substitutes another one', async () => {
  const update = vi.spyOn(alertsApi, 'updateRule').mockResolvedValue({ id: 5 })
  const gone = ref({ kind: 'measurement', deviceId: 99, endpointKey: 'battery', metric: 'batterySoc' })
  const stored: AlertRuleInput = { ...batteryLow, definition: { operator: 'and', children: [{ condition: {
    ...batteryLow.definition.children[0].condition!, signals: [gone] } }] } }
  show(<RuleEditor property={house} catalog={catalog} initial={stored} ruleId={5} revision={1} onSaved={() => undefined} onCancel={() => undefined} />)
  const condition = within(screen.getByRole('group', { name: 'Condition c1' }))
  const signal = condition.getByRole('combobox', { name: 'Signal' })
  expect(signal).toHaveValue('')
  expect(signal).toHaveDisplayValue('Unavailable signal (choose another)')
  expect(signal).toHaveAttribute('aria-invalid', 'true')
  expect(signal).toHaveAccessibleDescription(/no longer offers/)
  expect(condition.getByLabelText('Threshold (%)')).toHaveValue(35)
  expect(screen.getByText(/When an unavailable signal is below 35%/)).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Save rule' }))
  await waitFor(() => expect(update).toHaveBeenCalledTimes(1))
  expect(update.mock.calls[0][2].definition.children[0].condition!.signals).toEqual([gone])
})

it('gives every signal selector an accessible name and describes the latest reading', async () => {
  show(<RuleEditor property={house} catalog={catalog} initial={gridSetup[1]} onSaved={() => undefined} onCancel={() => undefined} />)
  const selectors = screen.getAllByRole('combobox', { name: 'Signal' })
  expect(selectors).toHaveLength(2)
  expect(selectors[1]).toHaveAccessibleDescription(/Latest reading 38%/)
  expect(selectors[1]).not.toHaveAttribute('aria-invalid')
  for (const selector of selectors)
    for (const option of within(selector).getAllByRole('option')) expect(option).toHaveAccessibleName(/\S/)
  expect(screen.getByRole('group', { name: 'Condition c1' })).toContainElement(screen.getByRole('group', { name: 'Also check' }))
})
