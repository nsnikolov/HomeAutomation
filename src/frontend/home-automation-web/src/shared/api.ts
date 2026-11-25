import type { DaikinOutcome, DaikinState, DaikinWaterHistory, Device, DevicePageKey, DevicePreferences, EwelinkRelayOutcome, EwelinkState, GrowattState, Health, Metric, Point, Property, PropertyState, Room, SensorHistory, SensorPeriod, TuyaState } from './types'
import type { DashboardGroup, DashboardSnapshot, DashboardSource } from './types'

export class ApiError extends Error {
  constructor(public readonly status: number, public readonly code?: string, public readonly detail?: string) {
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
    const dashboard = /^\/properties\/\d+\/dashboard$/.test(path)
    const error: { status?: unknown; detail?: unknown } | null = dashboard ||
      path.startsWith('/daikin/') || path.startsWith('/ewelink/') || path.startsWith('/tuya/')
      ? await response.json().catch(() => null) : null
    throw new ApiError(response.status, typeof error?.status === 'string' ? error.status : undefined,
      dashboard && (response.status === 400 || response.status === 503) && typeof error?.detail === 'string'
        ? error.detail : undefined)
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

async function providerAction<T>(provider: 'daikin' | 'ewelink' | 'tuya', id: number, action: string, body?: object) {
  const { token } = await request<{ token: string }>('/session/csrf')
  return request<T>(`/${provider}/${id}/${action}`, {
    method: 'POST', headers: { 'X-CSRF-TOKEN': token, ...(body && { 'Content-Type': 'application/json' }) },
    body: body && JSON.stringify(body),
  })
}

async function putWithCsrf(path: string, body: object) {
  const { token } = await request<{ token: string }>('/session/csrf')
  return request<void>(path, {
    method: 'PUT', headers: { 'X-CSRF-TOKEN': token, 'Content-Type': 'application/json' }, body: JSON.stringify(body),
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
  dashboard: (id: number, signal?: AbortSignal) => request<DashboardSnapshot>(`/properties/${id}/dashboard`, { signal }),
  dashboardSources: (id: number, signal?: AbortSignal) => request<DashboardSource[]>(`/properties/${id}/dashboard/sources`, { signal }),
  saveDashboard: (id: number, revision: string, groups: DashboardGroup[]) =>
    putWithCsrf(`/properties/${id}/dashboard`, { revision, groups }),
  growatt: (id: number, signal?: AbortSignal) => request<GrowattState>(`/properties/${id}/growatt`, { signal }),
  daikin: (id: number, signal?: AbortSignal) => request<DaikinState>(`/daikin/${id}`, { signal }),
  connectDaikin: (id: number) => providerAction<{ authorizationUrl: string }>('daikin', id, 'connect'),
  updateDaikin: (id: number, action: 'complete' | 'disconnect' | 'refresh') => providerAction<void>('daikin', id, action),
  assignDaikin: (id: number, device: string, roomId: number | null) =>
    providerAction<void>('daikin', id, `devices/${device}/assign`, { roomId }),
  commandDaikin: (id: number, device: string, operation: string, value: string, expectedMode?: string | null) =>
    providerAction<DaikinOutcome>('daikin', id, `devices/${device}/commands`, { operation, value, expectedMode }),
  ewelink: (id: number, signal?: AbortSignal) => request<EwelinkState>(`/ewelink/${id}`, { signal }),
  connectEwelink: (id: number) => providerAction<{ authorizationUrl: string }>('ewelink', id, 'connect'),
  updateEwelink: (id: number, action: 'disconnect' | 'refresh') => providerAction<void>('ewelink', id, action),
  prepareEwelinkElectricalViewer: async (id: number, tab: string) => {
    const { token } = await request<{ token: string }>('/session/csrf')
    return (active: boolean) => request<void>(`/ewelink/${id}/electrical-viewer`, {
      method: 'POST',
      headers: { 'X-CSRF-TOKEN': token, 'Content-Type': 'application/json' },
      body: JSON.stringify({ tab, active }),
      keepalive: !active,
      signal: AbortSignal.timeout(5_000),
    })
  },
  assignEwelink: (id: number, device: string, roomId: number | null) =>
    providerAction<void>('ewelink', id, `devices/${device}/assign`, { roomId }),
  commandEwelinkRelay: (id: number, device: string, on: boolean, confirmedProtected: boolean) =>
    providerAction<EwelinkRelayOutcome>('ewelink', id, `devices/${device}/relay`, { on, confirmedProtected }),
  setEwelinkRelayWrites: (id: number, device: string, enabled: boolean) =>
    providerAction<void>('ewelink', id, `devices/${device}/relay-preference`, { enabled }),
  tuya: (id: number, signal?: AbortSignal) => request<TuyaState>(`/tuya/${id}`, { signal }),
  tuyaHistory: (id: number, device: string, period: SensorPeriod, signal?: AbortSignal) =>
    request<SensorHistory>(`/tuya/${id}/devices/${device}/sensor-history?period=${period}`, { signal }),
  devicePreferences: (id: number, page: DevicePageKey, signal?: AbortSignal) =>
    request<DevicePreferences>(`/properties/${id}/device-preferences/${page}`, { signal }),
  orderDevices: (id: number, page: DevicePageKey, group: string, ids: string[]) =>
    putWithCsrf(`/properties/${id}/device-preferences/${page}/order`, { group, ids }),
  setTuyaVisibility: (id: number, device: string, hidden: boolean) =>
    putWithCsrf(`/properties/${id}/device-preferences/tuya/visibility/${device}`, { hidden }),
  refreshTuya: (id: number) => providerAction<void>('tuya', id, 'refresh'),
  assignTuya: (id: number, device: string, roomId: number | null) =>
    providerAction<void>('tuya', id, `devices/${device}/assign`, { roomId }),
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
