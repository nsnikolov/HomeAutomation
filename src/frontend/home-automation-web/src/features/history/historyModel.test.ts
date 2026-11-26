import { describe, expect, it } from 'vitest'
import type { LogicalRawPoint } from '../../shared/types'
import { bucket, segment } from './historyFixtures'
import {
  bucketRows, formatInstant, historyWindow, markerBucket, monthly, rawRows, replacementMarkers, summarize, zonedToUtc, type HistoryWindow,
} from './historyModel'

const sofia = 'Europe/Sofia'
const window = (value: ReturnType<typeof historyWindow>) => {
  if ('error' in value) throw new Error(value.error)
  return value as HistoryWindow
}

describe('property-local time', () => {
  it('converts local wall-clock times including non-whole-hour offsets', () => {
    expect(zonedToUtc('2026-06-01T00:00', 'Asia/Kolkata')).toBe(Date.parse('2026-05-31T18:30:00Z'))
    expect(zonedToUtc('2026-06-01T00:00', 'Asia/Kathmandu')).toBe(Date.parse('2026-05-31T18:15:00Z'))
    expect(zonedToUtc('2026-07-01T12:00', sofia)).toBe(Date.parse('2026-07-01T09:00:00Z'))
    expect(zonedToUtc('not a time', sofia)).toBeNull()
  })

  it('resolves repeated and skipped local times on DST changes', () => {
    expect(zonedToUtc('2026-10-25T03:30', sofia)).toBe(Date.parse('2026-10-25T00:30:00Z'))
    expect(zonedToUtc('2026-03-29T03:30', sofia)).toBe(Date.parse('2026-03-29T01:30:00Z'))
  })

  it('shows the UTC offset so a repeated local hour is unambiguous', () => {
    const first = formatInstant(Date.parse('2026-10-25T00:30:00Z'), sofia)
    const second = formatInstant(Date.parse('2026-10-25T01:30:00Z'), sofia)
    expect(first).not.toBe(second)
    expect(first.replace(/GMT.*$/, '')).toBe(second.replace(/GMT.*$/, ''))
  })
})

describe('history windows', () => {
  const now = Date.parse('2026-11-26T10:15:00Z')

  it('chooses bounded default resolutions for each preset', () => {
    const day = window(historyWindow('24h', now, sofia))
    expect(day.resolution).toBe('hour')
    expect(day.request).toEqual({ resolution: 'hour', from: '2026-11-25T10:15:00.000Z', to: '2026-11-26T10:15:00.000Z' })
    expect(window(historyWindow('24h', now, sofia, 'raw')).request.limit).toBe(5000)
    expect(window(historyWindow('7d', now, sofia)).resolution).toBe('hour')
    expect(window(historyWindow('30d', now, sofia)).request).toEqual({ resolution: 'day', fromDate: '2026-10-28', toDate: '2026-11-26' })
    const year = window(historyWindow('12m', now, sofia))
    expect(year.resolution).toBe('month')
    expect(year.request).toEqual({ resolution: 'day', fromDate: '2025-12-01', toDate: '2026-11-26' })
    expect(year.start).toBe(Date.parse('2025-11-30T22:00:00Z'))
    expect(window(historyWindow('12m', now, sofia, 'raw')).resolution).toBe('month')
  })

  it('uses property-local boundaries for custom ranges and rejects unbounded ones', () => {
    const custom = window(historyWindow('custom', now, 'Asia/Kathmandu', null, { from: '2026-11-20T00:00', to: '2026-11-22T00:00' }))
    expect(custom.request).toEqual({ resolution: 'hour', from: '2026-11-19T18:15:00.000Z', to: '2026-11-21T18:15:00.000Z' })
    expect(custom.allowed).toContain('raw')
    const long = window(historyWindow('custom', now, sofia, null, { from: '2026-02-01T00:00', to: '2026-11-01T00:00' }))
    expect(long.resolution).toBe('month')
    expect(long.request).toMatchObject({ resolution: 'day', fromDate: '2026-02-01', toDate: '2026-10-31' })
    expect(historyWindow('custom', now, sofia, null, { from: '2026-11-22T00:00', to: '2026-11-20T00:00' })).toHaveProperty('error')
    expect(historyWindow('custom', now, sofia, null, { from: '2024-01-01T00:00', to: '2026-11-01T00:00' })).toHaveProperty('error')
    expect(historyWindow('custom', now, sofia, 'hour', { from: '2026-01-01T00:00', to: '2026-11-01T00:00' })).toHaveProperty('error')
  })
})

describe('combination', () => {
  const now = Date.parse('2026-11-26T12:00:00Z')

  it('weights averages by covered time and keeps true extremes', () => {
    const summary = summarize([
      bucket('2026-11-26T08:00:00Z', { average: 10, minimum: 8, maximum: 12, coveredSeconds: 3600 }),
      bucket('2026-11-26T09:00:00Z', { average: 20, minimum: 15, maximum: 30, coveredSeconds: 1800, coverage: 0.5, quality: 'incomplete' }),
      bucket('2026-11-26T10:00:00Z', { average: null, minimum: null, maximum: null, coveredSeconds: 0, coverage: 0, quality: 'noData', sampleCount: 0 }),
    ], now)
    expect(summary.average).toBeCloseTo((10 * 3600 + 20 * 1800) / 5400)
    expect(summary.minimum).toBe(8)
    expect(summary.maximum).toBe(30)
    expect(summary.coverage).toBeCloseTo(5400 / 10800)
    expect([summary.incompleteBuckets, summary.noDataBuckets]).toEqual([1, 1])
  })

  it('builds months from 23- and 25-hour local days without treating partial months as complete', () => {
    const days = [
      bucket('2026-03-28T22:00:00Z', { date: '2026-03-29', durationSeconds: 23 * 3600, average: 10,
        energy: { status: 'measured', basis: 'counter', wh: 1000, positiveWh: null, negativeWh: null } }),
      bucket('2026-03-29T21:00:00Z', { date: '2026-03-30', durationSeconds: 24 * 3600, average: 20, coveredSeconds: 12 * 3600, coverage: 0.5,
        quality: 'incomplete', energy: { status: 'incomplete', basis: 'counter', wh: 500, positiveWh: null, negativeWh: null } }),
      bucket('2026-10-24T21:00:00Z', { date: '2026-10-25', durationSeconds: 25 * 3600, average: 5,
        energy: { status: 'measured', basis: 'counter', wh: 250, positiveWh: null, negativeWh: null } }),
    ]
    const [march, october] = monthly(days, Date.parse('2026-11-26T00:00:00Z'))
    expect(march.date).toBe('2026-03-01')
    expect(march.durationSeconds).toBe(47 * 3600)
    expect(march.average).toBeCloseTo((10 * 23 + 20 * 12) / 35)
    expect(march.energy).toMatchObject({ basis: 'counter', wh: 1500, status: 'incomplete' })
    expect(october.energy).toMatchObject({ wh: 250, status: 'measured' })
    expect(october.durationSeconds).toBe(25 * 3600)
  })

  it('keeps measured and estimated energy apart', () => {
    const measured = bucket('2026-11-26T08:00:00Z', { energy: { status: 'measured', basis: 'counter', wh: 400, positiveWh: null, negativeWh: null } })
    const estimated = bucket('2026-11-26T08:00:00Z', { energy: { status: 'estimated', basis: 'powerIntegration', wh: null, positiveWh: 450, negativeWh: 0 } })
    expect(summarize([measured], now)).toMatchObject({ measuredWh: 400, estimatedWh: null, measuredComplete: true })
    expect(summarize([estimated], now)).toMatchObject({ measuredWh: null, estimatedWh: 450, estimatedComplete: true })
    const [row] = bucketRows([estimated], 'hour', sofia, now)
    expect([row.measuredKwh, row.estimatedKwh]).toEqual([null, 0.45])
  })
})

describe('chart rows', () => {
  const now = Date.parse('2026-11-26T12:00:00Z')

  it('breaks lines for missing, unaggregated and unsupported periods but keeps real zeros', () => {
    const rows = bucketRows([
      bucket('2026-11-26T06:00:00Z', { average: 0, minimum: 0, maximum: 0 }),
      bucket('2026-11-26T07:00:00Z', { average: null, quality: 'noData', coveredSeconds: 0, coverage: 0 }),
      bucket('2026-11-26T08:00:00Z', { average: null, quality: 'notAggregated', coveredSeconds: 0, coverage: 0 }),
      bucket('2026-11-26T09:00:00Z', { average: null, quality: 'noData', flags: ['notReported'], coveredSeconds: 0, coverage: 0 }),
      bucket('2026-11-26T10:00:00Z', { average: 21, quality: 'incomplete', coverage: 0.4, coveredSeconds: 1440 }),
    ], 'hour', sofia, now)
    expect(rows.map(row => row.average)).toEqual([0, null, null, null, 21])
    expect(rows.map(row => row.quality)).toEqual(['complete', 'noData', 'notAggregated', 'notReported', 'incomplete'])
    expect(rows[0].band).toEqual([0, 0])
    expect(rows[1].band).toBeNull()
  })

  it('reports relay ON, OFF and uncovered hours separately', () => {
    const [row] = bucketRows([bucket('2026-11-26T08:00:00Z', { onSeconds: 1200, coveredSeconds: 2400, coverage: 2 / 3 })], 'hour', sofia, now)
    expect([row.onHours, row.offHours, row.unknownHours]).toEqual([1200 / 3600, 1200 / 3600, 1200 / 3600])
  })

  it('breaks raw lines at gaps longer than the hold and at every device change', () => {
    const point = (at: string, value: number, assignmentId: number): LogicalRawPoint => ({ observedAt: at, receivedAt: at, number: value,
      flag: null, code: null, timestampSource: 'provider', seriesId: assignmentId, assignmentId })
    const segments = [segment(1, 'Tuya A', '1970-01-01T00:00:00Z', '2026-11-26T14:37:00Z', 900),
      segment(2, 'eWeLink B', '2026-11-26T14:37:00Z', null, 120)]
    const rows = rawRows([point('2026-11-26T14:00:00Z', 20, 1), point('2026-11-26T14:10:00Z', 0, 1), point('2026-11-26T14:40:00Z', 21, 1),
      point('2026-11-26T14:41:00Z', 24, 2), point('2026-11-26T14:42:00Z', 24, 2), point('2026-11-26T14:50:00Z', 25, 2)], segments, 'gauge')
    expect(rows.map(row => row.value)).toEqual([20, 0, null, 21, null, 24, 24, null, 25])
  })
})

describe('replacement markers', () => {
  it('lists every replacement inside the window at its exact instant', () => {
    const segments = [segment(1, 'Tuya A', '1970-01-01T00:00:00Z', '2026-11-26T14:37:00Z'),
      segment(2, 'eWeLink B', '2026-11-26T14:37:00Z', '2026-11-26T18:05:00Z'), segment(3, 'Sonoff C', '2026-11-26T18:05:00Z', null)]
    const markers = replacementMarkers(segments, Date.parse('2026-11-26T12:00:00Z'), Date.parse('2026-11-26T20:00:00Z'))
    expect(markers.map(marker => [new Date(marker.at).toISOString(), marker.from, marker.to])).toEqual([
      ['2026-11-26T14:37:00.000Z', 'Tuya A', 'eWeLink B'], ['2026-11-26T18:05:00.000Z', 'eWeLink B', 'Sonoff C']])
    expect(replacementMarkers(segments, Date.parse('2026-11-26T19:00:00Z'), Date.parse('2026-11-26T20:00:00Z'))).toEqual([])
    const rows = bucketRows([bucket('2026-11-26T14:00:00Z'), bucket('2026-11-26T15:00:00Z')], 'hour', sofia, Date.parse('2026-11-27T00:00:00Z'))
    expect(markerBucket(rows, markers[0].at)).toBe(rows[0].label)
  })
})
