import type { HistoryBucket, LogicalHistorySegment } from '../../shared/types'

/** Synthetic backend buckets and segments for history tests. */
export function bucket(start: string, overrides: Partial<HistoryBucket> = {}): HistoryBucket {
  const begin = Date.parse(start)
  const durationSeconds = overrides.durationSeconds ?? 3600
  return {
    start, end: new Date(begin + durationSeconds * 1000).toISOString(), date: null, durationSeconds, quality: 'complete', coverage: 1,
    coveredSeconds: durationSeconds, sampleCount: 12, minimum: 18, maximum: 22, average: 20, mean: 20, firstObservedAt: start,
    lastObservedAt: start, lastValue: 20, onSeconds: null, transitions: null, energy: null, provisional: false, flags: [], seriesIds: [1],
    ...overrides,
  }
}

export function segment(id: number, name: string, from: string, to: string | null, hold = 900): LogicalHistorySegment {
  return { assignmentId: id, deviceId: id, deviceName: name, endpointId: null, from, to, status: 'available', seriesId: id,
    source: 'fixture', alternativeSeriesIds: [], maximumHoldSeconds: hold }
}
