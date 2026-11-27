import { clsx, type ClassValue } from 'clsx'
import { twMerge } from 'tailwind-merge'

export function cn(...values: ClassValue[]) {
  return twMerge(clsx(values))
}

/** PV power at or below this displays as 0 W: sensor noise or residual power at night. Mirrors the server's PvGeneration.Display. */
export const pvDisplayZeroAtOrBelowWatts = 10

/**
 * The number a live view shows for a reading. PV power from 0 to 10 W shows as 0, and negative PV power is invalid (null), never
 * production. Raw readings, history and energy keep their stored values.
 */
export function displayNumber(metric: string, value: number): number | null {
  if (metric !== 'pvPower') return value
  if (!Number.isFinite(value) || value < 0) return null
  return value <= pvDisplayZeroAtOrBelowWatts ? 0 : value
}

export function humanize(value: string) {
  return value.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, character => character.toUpperCase())
}

export function formatMoment(value: string, timeZone: string) {
  return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short', timeZone }).format(new Date(value))
}
