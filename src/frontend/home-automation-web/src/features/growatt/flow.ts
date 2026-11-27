// Pure helpers for the Growatt power-flow view (prompt 0069). They read the same Growatt state as the Details view; nothing here
// fetches, stores or estimates readings.
import type { Device, Metric, Reading } from '../../shared/types'
import { deviceAvailability } from '../Devices'

export const inverters = (devices: Device[]) => devices.filter(device => device.kind === 'inverter')

/** A metric's reading from any endpoint of the device. */
export function readingOf(device: Device | undefined, metric: Metric): Reading | undefined {
  return device?.endpoints.flatMap(endpoint => endpoint.measurements).find(reading => reading.metric === metric)
}

const usable = (reading: Reading | undefined) =>
  reading?.quality === 'fresh' && reading.number !== null && Number.isFinite(reading.number) ? reading.number : null

export type BatteryFlowState = 'charging' | 'discharging' | 'idle' | 'unavailable'
export type BatteryFlow = {
  state: BatteryFlowState
  /** Signed net flow in W: positive charges the battery, negative discharges it. */
  net: number | null
  /** Inverters with a current battery-flow reading, and how many inverters there are. */
  used: number; expected: number
  parts: { name: string; watts: number | null }[]
}

/**
 * The shared battery's flow as the sum of the inverters' own battery-flow readings (`batteryPower`, positive = charging). The
 * shared battery device has no flow reading of its own: Growatt reports `pBat` per inverter, and the adapter keeps it there so it is
 * never counted twice. Only current readings count; an inverter without one is listed as missing and makes the result partial.
 */
export function batteryFlow(devices: Device[]): BatteryFlow {
  const parts = inverters(devices).map(device => ({ name: device.name, watts: usable(readingOf(device, 'batteryPower')) }))
  const present = parts.filter(part => part.watts !== null)
  if (present.length === 0) return { state: 'unavailable', net: null, used: 0, expected: parts.length, parts }
  const net = present.reduce((sum, part) => sum + part.watts!, 0)
  return { state: net > 0 ? 'charging' : net < 0 ? 'discharging' : 'idle', net, used: present.length, expected: parts.length, parts }
}

export type DailyTotal = { total: number | null; partial: boolean; parts: { name: string; value: number | null }[] }

/** Today's counter summed over the inverters. Counters from an earlier day arrive as missing, so they never count. */
export function dailyTotal(devices: Device[], metric: Metric): DailyTotal {
  const parts = inverters(devices).map(device => {
    const reading = readingOf(device, metric)
    return { name: device.name, value: reading && reading.quality !== 'missing' && reading.number !== null ? reading.number : null }
  })
  const present = parts.filter(part => part.value !== null)
  return {
    total: present.length === 0 ? null : present.reduce((sum, part) => sum + part.value!, 0),
    partial: present.length > 0 && present.length < parts.length, parts,
  }
}

/**
 * Solar share of consumption: today's PV energy divided by today's inverter output energy, capped at 100 %. PV that charged the
 * battery and was used later is not attributed; the value is an indication, not an energy balance.
 */
export function solarShare(pv: number | null, consumption: number | null) {
  if (pv === null || consumption === null || consumption <= 0) return null
  return Math.min(100, Math.max(0, pv / consumption * 100))
}

export type DataStatus = { tone: 'good' | 'warn' | 'bad'; label: 'receiving' | 'stale' | 'none'; at: string | null }

/** One status for the view, from the inverters' availability and the newest source reading time. */
export function dataStatus(devices: Device[]): DataStatus {
  const list = inverters(devices)
  const latest = list.map(device => device.latestReadingAt ?? newestReading(device)).filter((at): at is string => !!at)
    .sort((a, b) => Date.parse(b) - Date.parse(a))[0] ?? null
  if (list.some(device => deviceAvailability(device) === 'online')) return { tone: 'good', label: 'receiving', at: latest }
  return latest ? { tone: 'warn', label: 'stale', at: latest } : { tone: 'bad', label: 'none', at: null }
}

function newestReading(device: Device) {
  return device.endpoints.flatMap(endpoint => endpoint.measurements).map(reading => reading.observedAt)
    .filter((at): at is string => !!at).sort((a, b) => Date.parse(b) - Date.parse(a))[0]
}

/** True when the provider's cloud flag says offline for any inverter, whatever the application availability is. */
export const cloudOffline = (devices: Device[]) => inverters(devices).some(device => device.reportedConnectivity === 'offline')
