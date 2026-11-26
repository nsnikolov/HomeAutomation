import type { HistoryBucket, HistoryQuality, LogicalHistorySegment, LogicalRawPoint, MetricAggregation } from '../../shared/types'
import type { LogicalHistoryRequest } from '../../shared/api'

/** Matches the backend threshold. Buckets and summaries below it are incomplete. */
export const incompleteBelow = 0.8
export const rawPointLimit = 5000
const hour = 3_600_000
const day = 24 * hour

export type RangePreset = '24h' | '7d' | '30d' | '12m' | 'custom'
export type ViewResolution = 'raw' | 'hour' | 'day' | 'month'
export type HistoryWindow = {
  preset: RangePreset; resolution: ViewResolution; allowed: ViewResolution[]; start: number; end: number
  request: Omit<LogicalHistoryRequest, 'channel' | 'metric'>
}
export type RangeError = { error: string }

// ---------------------------------------------------------------------------------------------------------------------
// Property-local calendar arithmetic. All instants are UTC milliseconds; local values are formatted in the property zone.

type Parts = { year: number; month: number; day: number; hour: number; minute: number; second: number }
const partFormatters = new Map<string, Intl.DateTimeFormat>()

export function localParts(instant: number, timeZone: string): Parts {
  let formatter = partFormatters.get(timeZone)
  if (!formatter) {
    formatter = new Intl.DateTimeFormat('en-US', {
      timeZone, hourCycle: 'h23', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit',
    })
    partFormatters.set(timeZone, formatter)
  }
  const values = Object.fromEntries(formatter.formatToParts(new Date(instant)).filter(part => part.type !== 'literal')
    .map(part => [part.type, Number(part.value)]))
  return { year: values.year, month: values.month, day: values.day, hour: values.hour % 24, minute: values.minute, second: values.second }
}

/** Offset of local wall-clock time from UTC at an instant, in milliseconds. */
export function zoneOffset(instant: number, timeZone: string) {
  const parts = localParts(instant, timeZone)
  const wall = Date.UTC(parts.year, parts.month - 1, parts.day, parts.hour, parts.minute, parts.second)
  return wall - Math.floor(instant / 1000) * 1000
}

export function localDate(instant: number, timeZone: string) {
  const { year, month, day } = localParts(instant, timeZone)
  return `${year}-${String(month).padStart(2, '0')}-${String(day).padStart(2, '0')}`
}

export function addDays(date: string, days: number) {
  const [year, month, dayOfMonth] = date.split('-').map(Number)
  return new Date(Date.UTC(year, month - 1, dayOfMonth + days)).toISOString().slice(0, 10)
}

export function daysBetween(from: string, to: string) {
  return Math.round((Date.parse(`${to}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) / day)
}

/**
 * Converts a property-local wall-clock time (YYYY-MM-DDTHH:mm) to a UTC instant. A repeated local time resolves to its
 * earlier occurrence; a skipped local time moves forward by the length of the gap (03:30 becomes 04:30 when 03:00–04:00
 * is skipped), matching the common "compatible" disambiguation.
 */
export function zonedToUtc(local: string, timeZone: string): number | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/.exec(local)
  if (!match) return null
  const [year, month, dayOfMonth, hours, minutes] = match.slice(1).map(Number)
  const wall = Date.UTC(year, month - 1, dayOfMonth, hours, minutes)
  const offsets = [...new Set([zoneOffset(wall - day, timeZone), zoneOffset(wall, timeZone), zoneOffset(wall + day, timeZone)])]
  const exact = offsets.map(offset => wall - offset).filter(instant => {
    const parts = localParts(instant, timeZone)
    return parts.year === year && parts.month === month && parts.day === dayOfMonth && parts.hour === hours && parts.minute === minutes
  })
  if (exact.length > 0) return Math.min(...exact)
  return wall - Math.min(...offsets)
}

export function localInput(instant: number, timeZone: string) {
  const parts = localParts(instant, timeZone)
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${parts.year}-${pad(parts.month)}-${pad(parts.day)}T${pad(parts.hour)}:${pad(parts.minute)}`
}

const formatters = new Map<string, Intl.DateTimeFormat>()
function formatter(timeZone: string, kind: string, options: Intl.DateTimeFormatOptions) {
  const key = `${timeZone}|${kind}`
  let value = formatters.get(key)
  if (!value) {
    value = new Intl.DateTimeFormat(undefined, { ...options, timeZone })
    formatters.set(key, value)
  }
  return value
}

/** Date and time with the UTC offset, so a repeated local hour on a DST change stays unambiguous. */
export function formatInstant(instant: number, timeZone: string) {
  // dateStyle and timeStyle cannot be combined with timeZoneName, so the fields are listed explicitly.
  return formatter(timeZone, 'instant', {
    year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', timeZoneName: 'shortOffset',
  }).format(instant)
}

export function formatDate(date: string) {
  return formatter('UTC', 'date', { dateStyle: 'medium' }).format(Date.parse(`${date}T12:00:00Z`))
}

export function formatMonth(month: string) {
  return formatter('UTC', 'month', { year: 'numeric', month: 'short' }).format(Date.parse(`${month}-15T12:00:00Z`))
}

export function bucketLabel(bucket: HistoryBucket, resolution: ViewResolution, timeZone: string) {
  if (resolution === 'month' && bucket.date) return formatMonth(bucket.date.slice(0, 7))
  if (resolution === 'day' && bucket.date) return formatDate(bucket.date)
  return formatter(timeZone, 'axis', { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }).format(Date.parse(bucket.start))
}

export function bucketTitle(bucket: HistoryBucket, resolution: ViewResolution, timeZone: string) {
  if (resolution === 'month' && bucket.date) return formatMonth(bucket.date.slice(0, 7))
  if (resolution === 'day' && bucket.date) {
    const hours = Math.round(bucket.durationSeconds / 3600)
    return hours === 24 ? formatDate(bucket.date) : `${formatDate(bucket.date)} (${hours}-hour day)`
  }
  return `${formatInstant(Date.parse(bucket.start), timeZone)} – ${formatter(timeZone, 'end', { timeStyle: 'short' }).format(Date.parse(bucket.end))}`
}

// ---------------------------------------------------------------------------------------------------------------------
// Ranges and bounded resolutions.

const maximumRange = 31 * day

export function historyWindow(preset: RangePreset, now: number, timeZone: string, override?: ViewResolution | null,
  custom?: { from: string; to: string }): HistoryWindow | RangeError {
  const today = localDate(now, timeZone)
  const instant = (start: number, end: number, resolution: ViewResolution, allowed: ViewResolution[]): HistoryWindow | RangeError => {
    const chosen = override && allowed.includes(override) ? override : resolution
    if (chosen === 'day' || chosen === 'month') {
      const fromDate = localDate(start, timeZone)
      const toDate = localDate(end - 1, timeZone)
      if (daysBetween(fromDate, toDate) + 1 > 366) return { error: 'Choose a range of at most 366 days.' }
      return { preset, resolution: chosen, allowed, start, end, request: { resolution: 'day', fromDate, toDate } }
    }
    return { preset, resolution: chosen, allowed, start, end, request: {
      resolution: chosen, from: new Date(start).toISOString(), to: new Date(end).toISOString(),
      ...(chosen === 'raw' ? { limit: rawPointLimit } : {}),
    } }
  }
  const dates = (fromDate: string, resolution: ViewResolution, allowed: ViewResolution[]): HistoryWindow => {
    const start = zonedToUtc(`${fromDate}T00:00`, timeZone) ?? now
    const chosen = override && allowed.includes(override) ? override : resolution
    if (chosen === 'hour') {
      const from = Math.max(start, now - maximumRange + hour)
      return { preset, resolution: 'hour', allowed, start: from, end: now,
        request: { resolution: 'hour', from: new Date(from).toISOString(), to: new Date(now).toISOString() } }
    }
    return { preset, resolution: chosen, allowed, start, end: now, request: { resolution: 'day', fromDate, toDate: today } }
  }
  switch (preset) {
    case '24h': return instant(now - day, now, 'hour', ['hour', 'raw'])
    case '7d': return instant(now - 7 * day, now, 'hour', ['hour', 'day'])
    case '30d': return dates(addDays(today, -29), 'day', ['day', 'hour'])
    case '12m': {
      const [year, month] = today.split('-').map(Number)
      const first = new Date(Date.UTC(year, month - 1 - 11, 1)).toISOString().slice(0, 10)
      return dates(first, 'month', ['month', 'day'])
    }
    case 'custom': {
      const start = custom ? zonedToUtc(custom.from, timeZone) : null
      const end = custom ? zonedToUtc(custom.to, timeZone) : null
      if (start === null || end === null) return { error: 'Enter a start and end date and time.' }
      if (end <= start) return { error: 'The end must be after the start.' }
      if (start >= now) return { error: 'The range must start in the past.' }
      const clipped = Math.min(end, now)
      const span = clipped - start
      const allowed: ViewResolution[] = span <= 3 * day ? ['hour', 'raw', 'day'] : span <= maximumRange ? ['hour', 'day']
        : span <= 92 * day ? ['day', 'month'] : ['month', 'day']
      if (override === 'hour' && span > maximumRange) return { error: 'Hourly history is limited to 31 days.' }
      return instant(start, clipped, span <= 7 * day ? 'hour' : span <= 92 * day ? 'day' : 'month', allowed)
    }
  }
}

// ---------------------------------------------------------------------------------------------------------------------
// Combination. These mirror the backend rules: averages are weighted by covered seconds, extremes are taken over every
// contributing bucket, and a total is complete only when every part is.

export type Summary = {
  average: number | null; minimum: number | null; maximum: number | null; coveredSeconds: number; elapsedSeconds: number
  coverage: number; sampleCount: number; onSeconds: number | null
  measuredWh: number | null; measuredComplete: boolean; estimatedWh: number | null; estimatedNegativeWh: number | null
  estimatedComplete: boolean; notAggregated: boolean; incompleteBuckets: number; noDataBuckets: number; buckets: number
}

export function elapsedSeconds(bucket: HistoryBucket, now: number) {
  return Math.max(0, (Math.min(Date.parse(bucket.end), now) - Date.parse(bucket.start)) / 1000)
}

export function summarize(buckets: readonly HistoryBucket[], now: number): Summary {
  let weighted = 0, weightedSeconds = 0, covered = 0, elapsed = 0, samples = 0
  let minimum: number | null = null, maximum: number | null = null, on: number | null = null
  let measured: number | null = null, estimated: number | null = null, negative: number | null = null
  let measuredComplete = buckets.length > 0, estimatedComplete = buckets.length > 0
  let notAggregated = false, incomplete = 0, noData = 0
  for (const bucket of buckets) {
    if (bucket.average !== null && bucket.coveredSeconds > 0) {
      weighted += bucket.average * bucket.coveredSeconds
      weightedSeconds += bucket.coveredSeconds
    }
    if (bucket.minimum !== null) minimum = minimum === null ? bucket.minimum : Math.min(minimum, bucket.minimum)
    if (bucket.maximum !== null) maximum = maximum === null ? bucket.maximum : Math.max(maximum, bucket.maximum)
    if (bucket.onSeconds !== null) on = (on ?? 0) + bucket.onSeconds
    covered += bucket.coveredSeconds
    elapsed += elapsedSeconds(bucket, now)
    samples += bucket.sampleCount
    if (bucket.quality === 'notAggregated') notAggregated = true
    if (bucket.quality === 'incomplete') incomplete++
    if (bucket.quality === 'noData') noData++
    const energy = bucket.energy
    if (energy?.basis === 'counter') {
      if (energy.wh !== null) measured = (measured ?? 0) + energy.wh
      if (energy.status !== 'measured') measuredComplete = false
    } else measuredComplete = false
    if (energy?.basis === 'powerIntegration') {
      if (energy.positiveWh !== null) estimated = (estimated ?? 0) + energy.positiveWh
      if (energy.negativeWh !== null) negative = (negative ?? 0) + energy.negativeWh
      if (energy.status !== 'estimated') estimatedComplete = false
    } else estimatedComplete = false
  }
  return {
    average: weightedSeconds > 0 ? weighted / weightedSeconds : null, minimum, maximum, coveredSeconds: covered, elapsedSeconds: elapsed,
    coverage: elapsed > 0 ? Math.min(1, covered / elapsed) : 0, sampleCount: samples, onSeconds: on,
    measuredWh: measured, measuredComplete, estimatedWh: estimated, estimatedNegativeWh: negative, estimatedComplete, notAggregated,
    incompleteBuckets: incomplete, noDataBuckets: noData, buckets: buckets.length,
  }
}

/** Calendar months built from property-local daily buckets. A month is complete only when its days together are. */
export function monthly(days: readonly HistoryBucket[], now: number): HistoryBucket[] {
  const groups = new Map<string, HistoryBucket[]>()
  for (const bucket of days) {
    const key = (bucket.date ?? bucket.start).slice(0, 7)
    groups.set(key, [...(groups.get(key) ?? []), bucket])
  }
  return [...groups.entries()].map(([month, items]) => {
    const summary = summarize(items, now)
    const quality: HistoryQuality = summary.notAggregated ? 'notAggregated'
      : items.every(item => item.quality === 'noData') ? 'noData' : summary.coverage < incompleteBelow ? 'incomplete' : 'complete'
    const counter = items.some(item => item.energy?.basis === 'counter')
    const power = items.some(item => item.energy?.basis === 'powerIntegration')
    const energy = counter ? {
      basis: 'counter' as const, wh: summary.measuredWh, positiveWh: null, negativeWh: null,
      status: summary.measuredWh === null ? 'noData' as const : summary.measuredComplete && quality === 'complete' ? 'measured' as const : 'incomplete' as const,
    } : power ? {
      basis: 'powerIntegration' as const, wh: null, positiveWh: summary.estimatedWh, negativeWh: summary.estimatedNegativeWh,
      status: summary.estimatedWh === null ? 'noData' as const : summary.estimatedComplete && quality === 'complete' ? 'estimated' as const : 'incomplete' as const,
    } : null
    const flags = [...new Set(items.flatMap(item => item.flags))]
    return {
      start: items[0].start, end: items.at(-1)!.end, date: `${month}-01`,
      durationSeconds: items.reduce((total, item) => total + item.durationSeconds, 0), quality, coverage: summary.coverage,
      coveredSeconds: summary.coveredSeconds, sampleCount: summary.sampleCount, minimum: summary.minimum, maximum: summary.maximum,
      average: summary.average, mean: null, firstObservedAt: items.find(item => item.firstObservedAt)?.firstObservedAt ?? null,
      lastObservedAt: items.findLast(item => item.lastObservedAt)?.lastObservedAt ?? null,
      lastValue: items.findLast(item => item.lastValue !== null)?.lastValue ?? null, onSeconds: summary.onSeconds,
      transitions: items.some(item => item.transitions !== null) ? items.reduce((total, item) => total + (item.transitions ?? 0), 0) : null,
      energy, provisional: items.some(item => item.provisional), flags,
      seriesIds: [...new Set(items.flatMap(item => item.seriesIds ?? []))],
    }
  })
}

// ---------------------------------------------------------------------------------------------------------------------
// Chart rows. Buckets without valid data produce null values so lines break instead of connecting across gaps.

export const qualityLabels: Record<HistoryQuality | 'notReported', string> = {
  complete: 'Complete', incomplete: 'Incomplete coverage', noData: 'No reading reported', notAggregated: 'Not yet aggregated',
  notReported: 'Not reported by the assigned device',
}
export const qualityHelp: Record<HistoryQuality | 'notReported', string> = {
  complete: 'At least 80% of the period is covered by valid readings.',
  incomplete: 'Less than 80% of the period is covered. Values describe only the covered part.',
  noData: 'No valid reading covers this period. This is not a zero reading.',
  notAggregated: 'History for this period has not been aggregated yet. Run the documented backfill to include it.',
  notReported: 'The device assigned for this period does not report this measurement.',
}

export function qualityOf(bucket: HistoryBucket): HistoryQuality | 'notReported' {
  return bucket.quality === 'noData' && bucket.flags.includes('notReported') ? 'notReported' : bucket.quality
}

export type BucketRow = {
  key: string; label: string; title: string; start: number; quality: HistoryQuality | 'notReported'; coverage: number
  average: number | null; band: [number, number] | null; minimum: number | null; maximum: number | null
  measuredKwh: number | null; measuredPartial: boolean; estimatedKwh: number | null; estimatedNegativeKwh: number | null
  estimatedPartial: boolean; onHours: number | null; offHours: number | null; unknownHours: number | null; bucket: HistoryBucket
}

export function bucketRows(buckets: readonly HistoryBucket[], resolution: ViewResolution, timeZone: string, now: number): BucketRow[] {
  return buckets.map(bucket => {
    const valued = bucket.quality === 'complete' || bucket.quality === 'incomplete'
    const energy = bucket.energy
    const elapsed = elapsedSeconds(bucket, now)
    const measured = energy?.basis === 'counter' && energy.wh !== null && energy.status !== 'noData' ? energy.wh / 1000 : null
    const estimated = energy?.basis === 'powerIntegration' && energy.positiveWh !== null && energy.status !== 'noData' ? energy.positiveWh / 1000 : null
    const negative = energy?.basis === 'powerIntegration' && energy.negativeWh ? -energy.negativeWh / 1000 : null
    return {
      key: bucket.start, label: bucketLabel(bucket, resolution, timeZone), title: bucketTitle(bucket, resolution, timeZone),
      start: Date.parse(bucket.start), quality: qualityOf(bucket), coverage: bucket.coverage,
      average: valued ? bucket.average : null,
      band: valued && bucket.minimum !== null && bucket.maximum !== null ? [bucket.minimum, bucket.maximum] : null,
      minimum: valued ? bucket.minimum : null, maximum: valued ? bucket.maximum : null,
      measuredKwh: measured, measuredPartial: measured !== null && energy?.status !== 'measured',
      estimatedKwh: estimated, estimatedNegativeKwh: negative, estimatedPartial: estimated !== null && energy?.status !== 'estimated',
      onHours: bucket.onSeconds !== null ? bucket.onSeconds / 3600 : null,
      offHours: bucket.onSeconds !== null ? Math.max(0, bucket.coveredSeconds - bucket.onSeconds) / 3600 : null,
      unknownHours: Math.max(0, elapsed - bucket.coveredSeconds) / 3600,
      bucket,
    }
  })
}

export type RawRow = { time: number; value: number | null; assignmentId: number | null; source: string | null }

/** Raw readings with explicit breaks at gaps longer than the source's hold and at every device change. */
export function rawRows(points: readonly LogicalRawPoint[], segments: readonly LogicalHistorySegment[], aggregation: MetricAggregation): RawRow[] {
  const holds = new Map(segments.map(segment => [segment.assignmentId, (segment.maximumHoldSeconds ?? 900) * 1000]))
  const sources = new Map(segments.map(segment => [segment.assignmentId, segment.source]))
  const rows: RawRow[] = []
  const sorted = [...points].sort((left, right) => Date.parse(left.observedAt) - Date.parse(right.observedAt))
  let previous: LogicalRawPoint | null = null
  for (const point of sorted) {
    const value = aggregation === 'boolean' ? (point.flag === null ? null : point.flag ? 1 : 0) : point.number
    if (value === null || !Number.isFinite(value)) continue
    const time = Date.parse(point.observedAt)
    if (previous) {
      const previousTime = Date.parse(previous.observedAt)
      const hold = holds.get(previous.assignmentId) ?? 900_000
      if (previous.assignmentId !== point.assignmentId || time - previousTime > hold)
        rows.push({ time: Math.min(previousTime + hold, time - 1), value: null, assignmentId: null, source: null })
    }
    rows.push({ time, value, assignmentId: point.assignmentId, source: sources.get(point.assignmentId) ?? null })
    previous = point
  }
  return rows
}

export type ReplacementMarker = { at: number; from: string; to: string; key: string }

/** Assignment boundaries within the window, from the backend's segment list. Provider names are never used to infer them. */
export function replacementMarkers(segments: readonly LogicalHistorySegment[], start: number, end: number): ReplacementMarker[] {
  const ordered = [...segments].sort((left, right) => Date.parse(left.from) - Date.parse(right.from))
  return ordered.slice(1).flatMap((segment, index) => {
    const at = Date.parse(segment.from)
    return at >= start && at < end ? [{ at, from: ordered[index].deviceName, to: segment.deviceName, key: String(segment.assignmentId) }] : []
  })
}

export function markerBucket(rows: readonly BucketRow[], at: number) {
  return rows.find(row => row.start <= at && at < Date.parse(row.bucket.end))?.label ?? null
}

// ---------------------------------------------------------------------------------------------------------------------
// Values.

const digits: Record<string, number> = { '°C': 1, '%': 0, W: 0, V: 1, A: 2, Hz: 2, kWh: 2, step: 0 }

export function formatValue(value: number | null | undefined, unit: string | null) {
  if (value === null || value === undefined || !Number.isFinite(value)) return '—'
  const fraction = unit === 'kWh' && Math.abs(value) < 1 ? 3 : unit === 'W' && Math.abs(value) < 10 ? 1 : digits[unit ?? ''] ?? 2
  const text = new Intl.NumberFormat(undefined, { maximumFractionDigits: fraction }).format(value)
  return unit === '%' ? `${text}%` : unit ? `${text} ${unit}` : text
}

export function formatPercent(value: number) {
  return new Intl.NumberFormat(undefined, { style: 'percent', maximumFractionDigits: 0 }).format(value)
}

export function formatHours(seconds: number | null) {
  if (seconds === null) return '—'
  const hours = Math.floor(seconds / 3600)
  const minutes = Math.round((seconds % 3600) / 60)
  return hours > 0 ? `${hours} h ${minutes} min` : `${minutes} min`
}

export const isEnergyMetric = (aggregation: MetricAggregation) => aggregation === 'dailyResetCounter' || aggregation === 'cumulativeCounter'
