import { fireEvent, render, screen, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { GrowattSummary } from './GrowattPanel'
import { DeviceBadge } from './Devices'
import { mergeHistory } from './Energy'
import type { Device, Point, Reading } from '../shared/types'

const property = { id: 1, name: 'Countryside House', timeZone: 'Europe/Sofia' }
const meter: Device = {
  id: 5, roomId: 1, name: 'Heater meter', manufacturer: 'Sonoff', model: 'Simulated switch',
  kind: 'smartSwitch', connectivity: 'online', faultCode: null, lastContactAt: null, quality: 'fresh', endpoints: [],
}

describe('Monitoring dashboard', () => {
  it('shows the unconfigured Growatt state without rendering fabricated readings', () => {
    render(<GrowattSummary result={{ status: 'notConfigured', lastSuccess: null, data: null }} timeZone="Europe/Sofia" />)
    expect(screen.getByText(/Live Growatt monitoring is not configured/)).toBeInTheDocument()
    expect(screen.queryByText('Solar installation')).not.toBeInTheDocument()
  })

  it('shows asymmetric recovery and keeps a provider offline flag distinct from reading freshness', () => {
    const observedAt = '2026-11-21T12:00:00Z'
    const output: Reading = {
      metric: 'acOutputVoltage', unit: 'V', observedAt, number: 241, flag: null, code: null, quality: 'fresh',
    }
    render(<GrowattSummary timeZone="Europe/Sofia" result={{ status: 'running', lastSuccess: observedAt, data: {
      installation: [], alerts: [{ deviceId: 2, deviceName: 'Growatt slave', code: 'offline', occurredAt: observedAt }],
      devices: [
        { ...meter, id: 1, kind: 'inverter', name: 'Growatt master' },
        { ...meter, id: 2, kind: 'inverter', name: 'Growatt slave', connectivity: 'offline',
          endpoints: [{ id: 2, name: 'Main', capabilities: [], measurements: [output] }] },
      ],
    } }} />)
    expect(screen.getByText(/Last successful synchronization/)).toBeInTheDocument()
    expect(screen.getByText(/Master online and Slave offline/)).toBeInTheDocument()
    expect(screen.getByText(/offline and has recently supplied readings/)).toBeInTheDocument()
    expect(screen.getByText('241 V')).toBeInTheDocument()
    expect(within(screen.getByRole('region', { name: 'Growatt master' }))
      .getByText('PV1 voltage').nextElementSibling).toHaveTextContent('Unavailable')
    expect(screen.getByRole('heading', { name: 'Recent Growatt alerts' })).toBeInTheDocument()
    expect(screen.queryByText('Outage')).not.toBeInTheDocument()
  })

  it('keeps the saved inverter order and distinguishes voltage, generation, and stale power', () => {
    const observedAt = '2026-11-21T12:00:00Z'
    const reading = (metric: Reading['metric'], unit: string, number: number, quality: Reading['quality'] = 'fresh'): Reading =>
      ({ metric, unit, observedAt, number, flag: null, code: null, quality })
    const update = vi.fn()
    render(<GrowattSummary timeZone={property.timeZone} preferences={{
      data: { order: { assigned: ['2', '5', '1'] }, hidden: [] }, ready: true, loading: false, busy: false, error: '', update,
    }} result={{ status: 'running', lastSuccess: observedAt, data: {
      installation: [reading('pvPower', 'W', 800)], alerts: [],
      devices: [
        { ...meter, id: 1, name: 'Growatt master', kind: 'inverter',
          endpoints: [{ id: 1, name: 'Main', capabilities: [], measurements: [
            reading('pv1Voltage', 'V', 109.4), reading('pvVoltage', 'V', 102), reading('pvPower', 'W', 800),
            reading('activePower', 'W', 303), reading('acChargingPower', 'W', 46), reading('gridVoltage', 'V', 230),
            reading('acInputPower', 'W', 349), reading('batteryPower', 'W', 45),
            reading('pvEnergyToday', 'kWh', 3.2), reading('outputEnergyToday', 'kWh', 4.3, 'stale'),
          ] }] },
        { ...meter, id: 2, name: 'Growatt slave', kind: 'inverter',
          endpoints: [
            { id: 2, name: 'PV 1', capabilities: [], measurements: [
              reading('pvVoltage', 'V', 105), reading('activePower', 'W', 0, 'stale'),
              reading('batteryPower', 'W', -35),
              reading('pvEnergyToday', 'kWh', 0),
            ] },
            { id: 3, name: 'PV 2', capabilities: [], measurements: [reading('pvPower', 'W', 70, 'stale')] },
          ] },
        { ...meter, name: 'Growatt shared battery', kind: 'battery' },
      ],
    } }} />)
    const master = screen.getByRole('region', { name: 'Growatt master' })
    const slave = screen.getByRole('region', { name: 'Growatt slave' })
    expect(slave.compareDocumentPosition(master) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(master.closest('.growatt-inverters')).toContainElement(slave)
    expect(within(master).getByText('PV generating')).toBeInTheDocument()
    expect(within(master).getByText('PV input power').nextElementSibling).toHaveTextContent('800 W')
    expect(within(master).getByText('PV1 voltage').nextElementSibling).toHaveTextContent('109.4 V')
    expect(within(slave).getByText('PV1 voltage').nextElementSibling).toHaveTextContent('105 V')
    expect(within(master).queryByText('Voltage', { exact: true })).not.toBeInTheDocument()
    expect(within(slave).queryByText('Current', { exact: true })).not.toBeInTheDocument()
    expect(within(master).getByText('Load/output power').nextElementSibling).toHaveTextContent('303 W')
    expect(within(master).getByText('AC charging power').nextElementSibling).toHaveTextContent('46 W')
    expect(within(master).getByText('Import from grid').nextElementSibling).toHaveTextContent('349 W')
    expect(within(slave).getByText('Import from grid').nextElementSibling).toHaveTextContent('Unavailable')
    expect(screen.getAllByRole('heading', { name: 'Energy today' })).toHaveLength(2)
    expect(within(master).getByText('PV generation', { exact: true }).nextElementSibling).toHaveTextContent('3.2 kWh')
    expect(within(master).getByText('Load/output energy').nextElementSibling).toHaveTextContent('4.3 kWh (stale)')
    expect(within(slave).getByText('PV generation', { exact: true }).nextElementSibling).toHaveTextContent('0 kWh')
    expect(within(slave).queryByText('Load/output energy')).not.toBeInTheDocument()
    expect(within(master).getByText('Battery charge/discharge').nextElementSibling).toHaveTextContent('Charging · 45 W')
    expect(within(slave).getByText('Battery charge/discharge').nextElementSibling).toHaveTextContent('Discharging · 35 W')
    expect(within(master).queryByText('Inverter output')).not.toBeInTheDocument()
    expect(within(slave).getByText('Load/output power').nextElementSibling).toHaveTextContent('0 W (stale)')
    expect(within(slave).getByText('PV voltage present')).toBeInTheDocument()
    expect(within(slave).getByText('PV readings stale')).toBeInTheDocument()
    expect(within(slave).getByText('70 W (stale)')).toBeInTheDocument()
    expect(within(slave).queryByText('PV generating')).not.toBeInTheDocument()
    expect(within(slave).getByRole('heading', { name: 'PV 1' })).toBeInTheDocument()
    expect(within(slave).getByRole('heading', { name: 'PV 2' })).toBeInTheDocument()
    fireEvent.keyDown(screen.getByRole('button', { name: 'Reorder Growatt slave' }), { key: 'ArrowDown' })
    expect(update).toHaveBeenCalledWith({ group: 'assigned', ids: ['5', '2', '1'] })
    expect(screen.getByRole('button', { name: 'Reorder Growatt shared battery' })).toBeInTheDocument()
    expect(screen.getByText('Shared battery flow unavailable')).toBeInTheDocument()
  })

  it('keeps nighttime zeros on both inverters and prefers fresh PV voltage over stale PV1', () => {
    const observedAt = '2026-11-23T20:00:00Z'
    const reading = (metric: Reading['metric'], unit: string, number: number, quality: Reading['quality'] = 'fresh'): Reading =>
      ({ metric, unit, observedAt, number, flag: null, code: null, quality })
    render(<GrowattSummary timeZone={property.timeZone} result={{ status: 'running', lastSuccess: observedAt, data: {
      installation: [], alerts: [], devices: ['master', 'slave'].map((role, index) => ({
        ...meter, id: index + 1, name: `Growatt ${role}`, kind: 'inverter',
        endpoints: [{ id: index + 1, name: 'Main', capabilities: [], measurements: [
          reading('pv1Voltage', 'V', 110, 'stale'), reading('pvVoltage', 'V', 0), reading('pvPower', 'W', 0),
          reading('acInputPower', 'W', 0, index === 0 ? 'fresh' : 'stale'), reading('batteryPower', 'W', 0),
        ] }],
      })),
    } }} />)
    for (const role of ['master', 'slave']) {
      const inverter = within(screen.getByRole('region', { name: `Growatt ${role}` }))
      expect(inverter.getByText('PV1 voltage').nextElementSibling).toHaveTextContent(/^0 V$/)
      expect(inverter.getByText('PV input power').nextElementSibling).toHaveTextContent(/^0 W$/)
      expect(inverter.getByText('No PV generation reported')).toBeInTheDocument()
      expect(inverter.getByText('Battery charge/discharge').nextElementSibling).toHaveTextContent('Idle · 0 W')
      expect(inverter.getByText('Import from grid').nextElementSibling).toHaveTextContent(
        role === 'master' ? /^0 W$/ : /^0 W \(stale\)$/)
    }
  })

  it('gives connectivity and telemetry freshness distinct labels', () => {
    render(<><DeviceBadge device={{ ...meter, connectivity: 'offline' }} /><DeviceBadge device={{ ...meter, quality: 'stale' }} /></>)
    expect(screen.getByText('Offline')).toBeInTheDocument()
    expect(screen.getByText('Stale')).toBeInTheDocument()
  })

  it('inserts gaps instead of inventing numeric history', () => {
    const points: Point[] = [
      { observedAt: '2026-11-21T12:00:00Z', number: 50, flag: null, code: null },
      { observedAt: '2026-11-21T12:01:00Z', number: 10, flag: null, code: null },
    ]
    expect(mergeHistory([{ metric: 'pvPower', points }], 10).map(point => point.pvPower)).toEqual([50, null, 10])
  })
})
