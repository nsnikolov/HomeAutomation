import type { DaikinOutcome, DaikinState, DaikinWaterHistory, Device, GrowattState, Health, Metric, Point, Property, PropertyState, Room } from './types'

export class ApiError extends Error {
  constructor(public readonly status: number, public readonly code?: string) {
    super(`Request failed (${status})`)
  }
}

async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
  const response = await fetch(`/api/v1${path}`, {
    ...options,
    headers: { Accept: 'application/json', ...options.headers },
    credentials: 'same-origin',
  })
  if (!response.ok) {
    const error: { status?: unknown } | null = path.startsWith('/daikin/') ? await response.json().catch(() => null) : null
    throw new ApiError(response.status, typeof error?.status === 'string' ? error.status : undefined)
  }
  return response.status === 204 ? undefined as T : await response.json() as T
}

async function sessionAction(path: '/login' | '/logout', body?: { email: string; password: string }) {
  const { token } = await request<{ token: string }>('/session/csrf')
  return request<void>(`/session${path}`, {
    method: 'POST',
    headers: { 'X-CSRF-TOKEN': token, ...(body && { 'Content-Type': 'application/json' }) },
    body: body && JSON.stringify(body),
  })
}

async function daikinAction<T>(id: number, action: string, body?: object) {
  const { token } = await request<{ token: string }>('/session/csrf')
  return request<T>(`/daikin/${id}/${action}`, {
    method: 'POST', headers: { 'X-CSRF-TOKEN': token, ...(body && { 'Content-Type': 'application/json' }) },
    body: body && JSON.stringify(body),
  })
}

export const api = {
  session: async (signal?: AbortSignal) => {
    try {
      return await request<{ email: string }>('/session', { signal })
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) return null
      throw error
    }
  },
  login: (email: string, password: string) => sessionAction('/login', { email, password }),
  logout: () => sessionAction('/logout'),
  properties: (signal?: AbortSignal) => request<Property[]>('/properties', { signal }),
  rooms: (id: number, signal?: AbortSignal) => request<Room[]>(`/properties/${id}/rooms`, { signal }),
  state: (id: number, signal?: AbortSignal) => request<PropertyState>(`/properties/${id}/state`, { signal }),
  growatt: (id: number, signal?: AbortSignal) => request<GrowattState>(`/properties/${id}/growatt`, { signal }),
  daikin: (id: number, signal?: AbortSignal) => request<DaikinState>(`/daikin/${id}`, { signal }),
  connectDaikin: (id: number) => daikinAction<{ authorizationUrl: string }>(id, 'connect'),
  updateDaikin: (id: number, action: 'complete' | 'disconnect' | 'refresh') => daikinAction<void>(id, action),
  assignDaikin: (id: number, device: string, roomId: number | null) =>
    daikinAction<void>(id, `devices/${device}/assign`, { roomId }),
  commandDaikin: (id: number, device: string, operation: string, value: string, expectedMode?: string | null) =>
    daikinAction<DaikinOutcome>(id, `devices/${device}/commands`, { operation, value, expectedMode }),
  daikinWaterHistory: (id: number, device: string, signal?: AbortSignal) =>
    request<DaikinWaterHistory>(`/daikin/${id}/devices/${device}/water-history`, { signal }),
  device: (id: number, device: number, signal?: AbortSignal) =>
    request<Device>(`/properties/${id}/devices/${device}`, { signal }),
  health: (signal?: AbortSignal) => request<Health>('/system/health', { signal }),
  history: (id: number, metric: Metric, hours: number, endpoint?: number, signal?: AbortSignal) => {
    const end = Date.now()
    const params = new URLSearchParams({
      metric,
      from: new Date(end - hours * 3600000).toISOString(),
      to: new Date(end).toISOString(),
    })
    if (endpoint !== undefined) params.set('endpointId', String(endpoint))
    return request<Point[]>(`/properties/${id}/telemetry?${params}`, { signal })
  },
}
