import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../shared/api'
import type { Device, GrowattState, InstallationValue, Reading } from '../../shared/types'
import { GrowattPanel, growattViewKey } from '../GrowattPanel'
import { GrowattFlow } from './GrowattFlow'

const at = '2026-11-27T10:42:00Z'
const reading = (metric: Reading['metric'], unit: string | null, number: number | null, quality: Reading['quality'] = 'fresh', code: string | null = null): Reading =>
  ({ metric, unit, observedAt: at, number, flag: null, code, quality })
const inverter = (id: number, name: string, measurements: Reading[], patch: Partial<Device> = {}): Device => ({
  id, roomId: null, name, kind: 'inverter', manufacturer: 'Growatt', model: 'SPF 5000 ES', connectivity: 'online', faultCode: null,
  lastContactAt: null, quality: 'fresh', availability: 'online', reportedConnectivity: 'offline', latestReadingAt: at,
  endpoints: [{ id, name: 'Main', capabilities: [], measurements }], ...patch,
})
const value = (patch: Partial<InstallationValue>): InstallationValue => ({
  slot: 'pvPower', basis: 'inverters', state: 'available', number: 0, unit: 'W', text: null, observedAt: at, used: 2, expected: 2, reason: null, ...patch,
})
function state(day: boolean): GrowattState {
  return { status: 'running', lastSuccess: at, data: { installation: [], alerts: [], devices: [
    inverter(1, 'Growatt master', [reading('pvVoltage', 'V', day ? 310 : 0), reading('pvPower', 'W', day ? 1200 : 1),
      reading('activePower', 'W', 300), reading('acInputPower', 'W', 387), reading('batteryPower', 'W', 63), reading('inverterLoadPercent', '%', 23),
      reading('gridFrequency', 'Hz', 50), reading('operatingMode', null, null, 'fresh', 'AC charge & Bypass'), reading('pvEnergyToday', 'kWh', 3.2),
      reading('outputEnergyToday', 'kWh', 4.3)],
    { pvInputs: [{ endpointId: 1, state: day ? 'generating' : 'notGenerating', reason: day ? 'aboveThreshold' : 'noVoltage', rawWatts: day ? 1200 : 1, displayWatts: day ? 1200 : 0 }] }),
    inverter(2, 'Growatt slave', [reading('pvPower', 'W', day ? 950 : 0), reading('activePower', 'W', 250), reading('acInputPower', 'W', 0),
      reading('batteryPower', 'W', 0), reading('inverterLoadPercent', '%', 18), reading('pvEnergyToday', 'kWh', 2.1)],
    { pvInputs: [{ endpointId: 2, state: day ? 'generating' : 'notGenerating', reason: 'belowThreshold', rawWatts: 0, displayWatts: 0 }] }),
    { ...inverter(9, 'Growatt shared battery', [reading('batterySoc', '%', 78), reading('voltage', 'V', 52.4)]), kind: 'battery' },
  ], solar: [
    value({ slot: 'pvPower', number: day ? 2150 : 0 }), value({ slot: 'consumption', number: 550 }),
    value({ slot: 'gridSupply', number: 244.5, unit: 'V', text: 'present' }), value({ slot: 'acInput', number: 387 }),
  ] } }
}

describe('Power-flow view', () => {
  it('shows the cross of nodes with values, the data status, the cloud flag and the battery sum from the inverters', () => {
    render(<GrowattFlow result={state(true)} timeZone="Europe/Sofia" />)
    expect(screen.getByText('Получава данни · 12:42')).toBeInTheDocument()
    expect(screen.getByText('Облак: offline')).toHaveAttribute('title', expect.stringContaining('само за информация'))
    const node = (title: string) => screen.getByText(title, { selector: 'strong' }).parentElement!
    expect(node('Солар')).toHaveTextContent('2,150 W')
    expect(node('Мрежа')).toHaveTextContent('Внос: 387 W244.5 V · 50 Hz')
    expect(node('Инвертор')).toHaveTextContent('AC charge & BypassMaster 23 % · Slave 18 %')
    expect(node('Дом')).toHaveTextContent('550 W')
    expect(node('Батерия')).toHaveTextContent('78 % · 52.4 VЗарежда 63 Wпо данни от инверторите')
    expect(screen.getByText('Мрежа към инвертора: 387 W')).toBeInTheDocument()
    expect(screen.getByText('Инвертор към батерията: 63 W')).toBeInTheDocument()
    const lines = document.querySelectorAll('.flow-line')
    expect([...lines].map(line => `${line.classList[1]}:${line.getAttribute('data-direction')}`))
      .toEqual(['flow-solar:forward', 'flow-grid:forward', 'flow-home:forward', 'flow-battery:forward'])
    expect(document.querySelectorAll('.flow-line.flowing .flow-chevron')).toHaveLength(12)
    expect(document.querySelectorAll('svg.flow-illustration[aria-hidden="true"]')).toHaveLength(5)
  })

  it('mutes the solar node at night, shows no production and draws idle lines as static dashed tracks', () => {
    const night = state(false)
    night.data!.devices[0].endpoints[0].measurements = night.data!.devices[0].endpoints[0].measurements
      .map(item => item.metric === 'batteryPower' ? { ...item, number: -400 } : item)
    render(<GrowattFlow result={night} timeZone="Europe/Sofia" />)
    expect(screen.getByText('Солар', { selector: 'strong' }).parentElement).toHaveTextContent('Няма производство')
    expect(screen.getByText('Солар', { selector: 'strong' }).closest('.flow-node')).not.toHaveClass('active')
    const solar = document.querySelector('.flow-solar')!
    expect(solar).toHaveAttribute('data-direction', 'none')
    expect(solar.querySelector('.flow-chevron')).toBeNull()
    expect(screen.getByText('Солар към инвертора: няма поток')).toBeInTheDocument()
    expect(document.querySelector('.flow-battery')).toHaveAttribute('data-direction', 'reverse')
    expect(screen.getByText('Батерия', { selector: 'strong' }).parentElement).toHaveTextContent('Разрежда 400 W')
  })

  it('shows today’s totals with master and slave, the share of solar, and short inverter cards', () => {
    render(<GrowattFlow result={state(true)} timeZone="Europe/Sofia" />)
    const today = within(screen.getByRole('region', { name: 'Днес' }))
    expect(today.getByText('Произведено от солар').parentElement).toHaveTextContent('5.3 kWhMaster 3.2 kWh + Slave 2.1 kWh')
    expect(today.getByText('Консумация').parentElement).toHaveTextContent('4.3 kWh (частично)Master 4.3 kWh + Slave —')
    expect(today.getByText('Разредено от батерия').parentElement).toHaveTextContent('—')
    expect(today.getByText('Дял на солара').parentElement).toHaveTextContent('100 %')
    const master = within(screen.getByRole('region', { name: 'Growatt master' }))
    expect(master.getByText('Онлайн')).toBeInTheDocument()
    expect(master.getByText('Товар').nextElementSibling).toHaveTextContent('300 W')
    expect(master.getByText('Внос от мрежата').nextElementSibling).toHaveTextContent('387 W')
    expect(master.getByText('Батерия').nextElementSibling).toHaveTextContent('Зарежда 63 W')
    expect(master.getByRole('meter', { name: 'Натоварване на Growatt master' })).toHaveAttribute('aria-valuenow', '23')
    expect(master.getByText('Всички показания')).toBeInTheDocument()
  })

  it('says when there is no data and never shows stale status as receiving', () => {
    const empty = state(true)
    empty.data!.devices = empty.data!.devices.map(device => device.kind === 'inverter'
      ? { ...device, availability: 'unknown', latestReadingAt: null, reportedConnectivity: 'unknown', endpoints: [] } : device)
    render(<GrowattFlow result={empty} timeZone="Europe/Sofia" />)
    expect(screen.getByText('Няма данни')).toBeInTheDocument()
    expect(screen.queryByText('Облак: offline')).not.toBeInTheDocument()
    expect(screen.getByText('Батерия', { selector: 'strong' }).parentElement).toHaveTextContent('Потокът не е наличен')
  })
})

describe('Flow and Details switch', () => {
  beforeEach(() => {
    localStorage.clear()
    vi.spyOn(api, 'growatt').mockResolvedValue(state(true))
    vi.spyOn(api, 'devicePreferences').mockResolvedValue({ order: {}, hidden: [] } as never)
  })
  afterEach(() => { vi.restoreAllMocks(); localStorage.clear() })

  function Where() {
    const location = useLocation()
    return <output aria-label="Location">{location.search}</output>
  }
  function show(path = '/properties/1/growatt') {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    return render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[path]}>
      <Routes><Route path="*" element={<><GrowattPanel property={{ id: 1, name: 'Countryside House', timeZone: 'Europe/Sofia' }} /><Where /></>} /></Routes>
    </MemoryRouter></QueryClientProvider>)
  }

  it('opens the power-flow view by default and records it in the URL and localStorage', async () => {
    show()
    expect(await screen.findByRole('region', { name: 'Поток на енергията' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Поток' })).toHaveAttribute('aria-pressed', 'true')
    await waitFor(() => expect(screen.getByRole('status', { name: 'Location' })).toHaveTextContent('?view=flow'))
    expect(localStorage.getItem(growattViewKey)).toBe('flow')
  })

  it('switches to Details, which renders the existing Growatt summary, keeps the choice and fetches once', async () => {
    const view = show()
    await screen.findByRole('region', { name: 'Поток на енергията' })
    const details = screen.getByRole('button', { name: 'Детайли' })
    details.focus()
    expect(details).toHaveFocus()
    fireEvent.click(details)
    expect(await screen.findByRole('heading', { name: 'Growatt monitoring' })).toBeInTheDocument()
    expect(screen.getByText('Solar installation')).toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'Growatt master' })).toHaveTextContent('PV generating')
    expect(screen.queryByRole('region', { name: 'Поток на енергията' })).not.toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('status', { name: 'Location' })).toHaveTextContent('?view=details'))
    expect(localStorage.getItem(growattViewKey)).toBe('details')
    expect(api.growatt).toHaveBeenCalledTimes(1)
    view.unmount()

    // A reload without a view in the URL restores the stored choice; an explicit URL wins over storage.
    show()
    expect(await screen.findByRole('heading', { name: 'Growatt monitoring' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Детайли' })).toHaveAttribute('aria-pressed', 'true')
  })

  it('lets the URL choose the view over the stored one', async () => {
    localStorage.setItem(growattViewKey, 'details')
    show('/properties/1/growatt?view=flow')
    expect(await screen.findByRole('region', { name: 'Поток на енергията' })).toBeInTheDocument()
    expect(localStorage.getItem(growattViewKey)).toBe('flow')
  })
})
