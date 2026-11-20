import type { Device, Health, Metric, Point, Property, PropertyState, Room } from './types'

export class ApiError extends Error {
  constructor(public readonly status: number) {
    super(`Request failed (${status})`)
  }
}

async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
  const response = await fetch(`/api/v1${path}`, {
    ...options,
    headers: { Accept: 'application/json', ...options.headers },
    credentials: 'same-origin',
  })
  if (!response.ok) throw new ApiError(response.status)
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
