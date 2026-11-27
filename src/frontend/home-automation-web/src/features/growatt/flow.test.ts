import { describe, expect, it } from 'vitest'
import type { Device, Point, PropertyState, Reading } from '../../shared/types'
import { productionCurves, sumSeries } from '../Energy'
import { batteryFlow, cloudOffline, dailyTotal, dataStatus, solarShare } from './flow'

const at = '2026-11-27T12:00:00Z'
const reading = (metric: Reading['metric'], number: number | null, quality: Reading['quality'] = 'fresh', observedAt = at): Reading =>
  ({ metric, unit: null, observedAt, number, flag: null, code: null, quality })
const inverter = (id: number, name: string, measurements: Reading[], patch: Partial<Device> = {}): Device => ({
  id, roomId: null, name, kind: 'inverter', manufacturer: 'Growatt', model: 'SPF 5000 ES', connectivity: 'online', faultCode: null,
  lastContactAt: null, quality: 'fresh', endpoints: [{ id, name: 'Main', capabilities: [], measurements }], ...patch,
})
const battery: Device = { ...inverter(9, 'Growatt shared battery', [reading('batterySoc', 78), reading('voltage', 52.4)]), kind: 'battery' }

describe('battery flow from the inverters', () => {
  it('sums both inverters: master charging 63 W and slave idle charge the battery with 63 W', () => {
    const flow = batteryFlow([inverter(1, 'Growatt master', [reading('batteryPower', 63)]), inverter(2, 'Growatt slave', [reading('batteryPower', 0)]), battery])
    expect(flow).toEqual({ state: 'charging', net: 63, used: 2, expected: 2,
      parts: [{ name: 'Growatt master', watts: 63 }, { name: 'Growatt slave', watts: 0 }] })
  })

  it('reports discharging and idle from the signed sum', () => {
    expect(batteryFlow([inverter(1, 'M', [reading('batteryPower', -400)]), inverter(2, 'S', [reading('batteryPower', -350)])]))
      .toMatchObject({ state: 'discharging', net: -750 })
    expect(batteryFlow([inverter(1, 'M', [reading('batteryPower', 120)]), inverter(2, 'S', [reading('batteryPower', -120)])]))
      .toMatchObject({ state: 'idle', net: 0 })
    expect(batteryFlow([inverter(1, 'M', [reading('batteryPower', 0)]), inverter(2, 'S', [reading('batteryPower', 0)])]))
      .toMatchObject({ state: 'idle', net: 0, used: 2 })
  })

  it('uses only one inverter when the other has no current reading, and says so', () => {
    const flow = batteryFlow([inverter(1, 'M', [reading('batteryPower', -90)]), inverter(2, 'S', [reading('batteryPower', 500, 'stale')])])
    expect(flow).toMatchObject({ state: 'discharging', net: -90, used: 1, expected: 2 })
    expect(flow.parts[1]).toEqual({ name: 'S', watts: null })
    expect(batteryFlow([inverter(1, 'M', [reading('batteryPower', 40)]), inverter(2, 'S', [])])).toMatchObject({ used: 1, net: 40 })
  })

  it('is unavailable when no inverter reports a current flow, and ignores the shared battery device', () => {
    expect(batteryFlow([inverter(1, 'M', []), inverter(2, 'S', [reading('batteryPower', 10, 'missing')]), battery]))
      .toEqual({ state: 'unavailable', net: null, used: 0, expected: 2, parts: [{ name: 'M', watts: null }, { name: 'S', watts: null }] })
    expect(batteryFlow([battery])).toMatchObject({ state: 'unavailable', expected: 0 })
  })
})

describe('today and status', () => {
  const devices = [
    inverter(1, 'Growatt master', [reading('pvEnergyToday', 3.2), reading('outputEnergyToday', 4.3), reading('batteryDischargedEnergyToday', 1.1)]),
    inverter(2, 'Growatt slave', [reading('pvEnergyToday', 2.1), reading('outputEnergyToday', null, 'missing')]),
  ]

  it('sums today’s counters per inverter and marks partial totals', () => {
    expect(dailyTotal(devices, 'pvEnergyToday')).toEqual({ total: 5.300000000000001, partial: false,
      parts: [{ name: 'Growatt master', value: 3.2 }, { name: 'Growatt slave', value: 2.1 }] })
    expect(dailyTotal(devices, 'outputEnergyToday')).toMatchObject({ total: 4.3, partial: true })
    expect(dailyTotal([inverter(1, 'M', [])], 'pvEnergyToday')).toMatchObject({ total: null, partial: false })
  })

  it('caps the solar share at 100 % and leaves it empty without consumption', () => {
    expect(solarShare(2, 4)).toBe(50)
    expect(solarShare(9, 4)).toBe(100)
    expect(solarShare(null, 4)).toBeNull()
    expect(solarShare(2, 0)).toBeNull()
  })

  it('derives one data status from availability and the newest source time, with the cloud flag kept separate', () => {
    const online = inverter(1, 'M', [reading('pvPower', 1)], { availability: 'online', latestReadingAt: at, reportedConnectivity: 'offline' })
    const stale = inverter(2, 'S', [reading('pvPower', 1, 'stale', '2026-11-27T11:00:00Z')], { availability: 'stale', latestReadingAt: '2026-11-27T11:00:00Z' })
    expect(dataStatus([online, stale])).toEqual({ tone: 'good', label: 'receiving', at })
    expect(dataStatus([stale])).toEqual({ tone: 'warn', label: 'stale', at: '2026-11-27T11:00:00Z' })
    expect(dataStatus([inverter(3, 'X', [], { availability: 'unknown' })])).toEqual({ tone: 'bad', label: 'none', at: null })
    expect(cloudOffline([online, stale])).toBe(true)
    expect(cloudOffline([stale])).toBe(false)
  })
})

describe('Energy curves from recorded inverter series', () => {
  const point = (observedAt: string, number: number | null): Point => ({ observedAt, number, flag: null, code: null })

  it('sums the inverters at matching times and leaves a gap where one is missing', () => {
    const master = [point('2026-11-27T12:00:03Z', 1200), point('2026-11-27T12:05:02Z', 1100), point('2026-11-27T12:10:00Z', 900)]
    const slave = [point('2026-11-27T12:00:05Z', 950), point('2026-11-27T12:10:04Z', 0)]
    expect(sumSeries([master, slave], 150)).toEqual([
      point('2026-11-27T12:00:05.000Z', 2150), point('2026-11-27T12:10:04.000Z', 900),
    ])
    expect(sumSeries([master], 150).map(item => item.number)).toEqual([1200, 1100, 900])
    expect(sumSeries([], 150)).toEqual([])
    expect(sumSeries([[point(at, null)], [point(at, 5)]], 150)).toEqual([])
  })

  it('uses the installation series when the provider records one, otherwise the inverters’ own series', () => {
    const state = (installation: Reading[]): PropertyState => ({ installation, alerts: [], devices: [
      inverter(1, 'M', [reading('pvPower', 900), reading('activePower', 300)]),
      inverter(2, 'S', [reading('pvPower', 800), reading('activePower', 250)]), battery,
    ] })
    expect(productionCurves(state([]))).toMatchObject([
      { metric: 'pvPower', label: 'PV production (sum of inverters)', endpoints: [1, 2] },
      { metric: 'activePower', label: 'Inverter output (sum of inverters)', endpoints: [1, 2] },
    ])
    expect(productionCurves(state([reading('pvPower', 1700), reading('householdPower', 600)]))).toMatchObject([
      { metric: 'pvPower', label: 'PV production' }, { metric: 'householdPower', label: 'Consumption' },
    ])
    expect(productionCurves(state([reading('pvPower', 1700)]))[0].endpoints).toBeUndefined()
    expect(productionCurves(undefined).map(curve => curve.metric)).toEqual(['pvPower', 'householdPower'])
  })
})
