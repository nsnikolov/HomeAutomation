import type { DaikinOutcome, DaikinState, DaikinWaterHistory, Device, DevicePageKey, DevicePreferences, EwelinkRelayOutcome, EwelinkState, GrowattState, Health, Metric, Point, Property, PropertyState, Room, SensorHistory, SensorPeriod, TuyaState } from './types'
import type { DashboardGroup, DashboardSnapshot, DashboardSource } from './types'
import type { ConfigurationView, ProviderConfigurationUpdate } from './types'
import type {
  AssignDeviceRequest, DeviceAuditEntry, DeviceTransferImpact, LogicalBindingReview, LogicalDeviceInput, LogicalDeviceList, LogicalHistory,
  ManagedRoom, PhysicalDeviceList, PropertyDeletionImpact, PropertyManagementView, ReleasePreview, TransferableDevice,
  ReplaceDeviceRequest, ReplacementCandidate,
} from './types'

/** Bounded logical history request. Raw and hourly use UTC instants; daily uses property-local calendar dates. */
export type LogicalHistoryRequest = {
  channel: string; metric: string; resolution: 'raw' | 'hour' | 'day'
  from?: string; to?: string; fromDate?: string; toDate?: string; limit?: number
}

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

/** Problem responses from logical-device routes carry a title for people and a stable code and missing capabilities. */
export class LogicalApiError extends Error {
  constructor(public readonly status: number, public readonly code: string | undefined, message: string,
    public readonly missing: string[] = []) {
    super(message)
  }
}

async function logicalRequest<T>(path: string, options: RequestInit = {}): Promise<T> {
  const response = await fetch(`/api/v1${path}`, {
    ...options, headers: { Accept: 'application/json', ...options.headers }, credentials: 'same-origin',
  })
  if (!response.ok) {
    const problem: { title?: unknown; code?: unknown; missing?: unknown } | null = await response.json().catch(() => null)
    const fallback = response.status === 503 ? 'Logical devices are not available until the database is upgraded.'
      : response.status === 403 ? 'Only property Owners and Admins can change logical devices.'
        : response.status === 409 ? 'The device changed in the meantime. Reload and try again.' : `Request failed (${response.status})`
    throw new LogicalApiError(response.status, typeof problem?.code === 'string' ? problem.code : undefined,
      typeof problem?.title === 'string' && problem.title ? problem.title : fallback,
      Array.isArray(problem?.missing) ? problem.missing.filter((item): item is string => typeof item === 'string') : [])
  }
  return response.status === 204 ? undefined as T : await response.json() as T
}

async function logicalPost<T>(path: string, body: object | null, method: 'POST' | 'PUT' | 'DELETE' = 'POST') {
  const { token } = await request<{ token: string }>('/session/csrf')
  return logicalRequest<T>(path, {
    method, headers: { 'X-CSRF-TOKEN': token, 'Content-Type': 'application/json' }, body: body === null ? undefined : JSON.stringify(body),
  })
}

const configurationPath = (property: number | null) => property === null
  ? '/configuration/global' : `/configuration/properties/${property}`

export const api = {
  configuration: (property: number | null, signal?: AbortSignal) =>
    request<ConfigurationView>(configurationPath(property), { signal }),
  saveConfiguration: (property: number | null, provider: string, update: ProviderConfigurationUpdate) =>
    putWithCsrf(`${configurationPath(property)}/${encodeURIComponent(provider)}`, update),
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
  propertyManagement: (signal?: AbortSignal) => logicalRequest<PropertyManagementView>('/property-management', { signal }),
  createProperty: (body: { name: string; description: string | null; timeZone: string | null; requestId: string }) =>
    logicalPost<{ id: number }>('/property-management/properties', body),
  updateProperty: (id: number, body: { name: string; description: string | null; timeZone: string }) =>
    logicalPost<{ id: number }>(`/properties/${id}/details`, body, 'PUT'),
  deletionImpact: (id: number, signal?: AbortSignal) => logicalRequest<PropertyDeletionImpact>(`/properties/${id}/deletion-impact`, { signal }),
  deleteProperty: (id: number, confirmName: string) => logicalPost<{ id: number }>(`/properties/${id}/delete`, { confirmName }),
  managedRooms: (id: number, signal?: AbortSignal) => logicalRequest<ManagedRoom[]>(`/properties/${id}/room-management`, { signal }),
  createRoom: (id: number, body: { name: string; description: string | null }) =>
    logicalPost<{ id: number }>(`/properties/${id}/room-management`, body),
  updateRoom: (id: number, room: number, body: { name: string; description: string | null }) =>
    logicalPost<{ id: number }>(`/properties/${id}/room-management/${room}`, body, 'PUT'),
  deleteRoom: (id: number, room: number) => logicalPost<{ id: number }>(`/properties/${id}/room-management/${room}`, null, 'DELETE'),
  renameDevice: (id: number, device: number, displayName: string | null) =>
    logicalPost<{ id: number }>(`/properties/${id}/devices/${device}/name`, { displayName }, 'PUT'),
  setDeviceRoom: (id: number, device: number, roomId: number | null) =>
    logicalPost<{ id: number }>(`/properties/${id}/devices/${device}/room`, { roomId }, 'PUT'),
  deviceAudit: (id: number, device: number, signal?: AbortSignal) =>
    logicalRequest<DeviceAuditEntry[]>(`/properties/${id}/devices/${device}/audit`, { signal }),
  transferableDevices: (id: number, signal?: AbortSignal) =>
    logicalRequest<TransferableDevice[]>(`/properties/${id}/transferable-devices`, { signal }),
  transferImpact: (id: number, device: number, destination: number, signal?: AbortSignal) =>
    logicalRequest<DeviceTransferImpact>(`/properties/${id}/devices/${device}/transfer-impact?destination=${destination}`, { signal }),
  transferDevice: (id: number, device: number, destinationPropertyId: number) =>
    logicalPost<{ id: number }>(`/properties/${id}/devices/${device}/transfer`, { destinationPropertyId }),
  logicalDevices: (id: number, signal?: AbortSignal) =>
    logicalRequest<LogicalDeviceList>(`/properties/${id}/logical-devices?includeArchived=true`, { signal }),
  replacementCandidates: (id: number, logical: number, channel: string, signal?: AbortSignal) =>
    logicalRequest<ReplacementCandidate[]>(`/properties/${id}/logical-devices/${logical}/candidates?channel=${encodeURIComponent(channel)}`, { signal }),
  physicalDevices: (id: number, signal?: AbortSignal) =>
    logicalRequest<PhysicalDeviceList>(`/properties/${id}/logical-devices/physical-devices`, { signal }),
  createLogicalDevice: (id: number, body: LogicalDeviceInput & { deviceId: number | null; endpointId: number | null }) =>
    logicalPost<{ id: number }>(`/properties/${id}/logical-devices`, { ...body, channelKey: null, effectiveAt: null, reason: null }),
  updateLogicalDevice: (id: number, logical: number, body: LogicalDeviceInput & { expectedUpdatedAt: string }) =>
    logicalPost<{ id: number }>(`/properties/${id}/logical-devices/${logical}`, body, 'PUT'),
  assignDevice: (id: number, logical: number, body: AssignDeviceRequest) =>
    logicalPost<{ id: number }>(`/properties/${id}/logical-devices/${logical}/assignments`, body),
  unassignDevice: (id: number, logical: number, body: { channelKey: string; expectedAssignmentId: number; effectiveAt: string; reason: string | null }) =>
    logicalPost<{ id: number }>(`/properties/${id}/logical-devices/${logical}/unassign`, body),
  archiveLogicalDevice: (id: number, logical: number, reason: string | null) =>
    logicalPost<{ id: number }>(`/properties/${id}/logical-devices/${logical}/archive`, { reason }),
  restoreLogicalDevice: (id: number, logical: number, reason: string | null) =>
    logicalPost<{ id: number }>(`/properties/${id}/logical-devices/${logical}/restore`, { reason }),
  /** What using the hardware here would end elsewhere; null when no other logical device uses it at that time. */
  releasePreview: async (id: number, logical: number, deviceId: number, endpointId: number | null, effectiveAt: string, signal?: AbortSignal) =>
    (await logicalRequest<ReleasePreview | undefined>(`/properties/${id}/logical-devices/${logical}/release-preview?${new URLSearchParams({
      deviceId: String(deviceId), effectiveAt, ...(endpointId === null ? {} : { endpointId: String(endpointId) }) })}`, { signal })) ?? null,
  replaceDevice: (id: number, logical: number, body: ReplaceDeviceRequest) =>
    logicalPost<{ id: number }>(`/properties/${id}/logical-devices/${logical}/replace`, body),
  logicalHistory: (id: number, logical: number, request: LogicalHistoryRequest, signal?: AbortSignal) => {
    const params = new URLSearchParams()
    for (const [key, value] of Object.entries(request)) if (value !== undefined && value !== null) params.set(key, String(value))
    return logicalRequest<LogicalHistory>(`/properties/${id}/logical-devices/${logical}/history?${params}`, { signal })
  },
  logicalBindings: (id: number, signal?: AbortSignal) =>
    logicalRequest<LogicalBindingReview>(`/properties/${id}/dashboard/logical-bindings`, { signal }),
  convertLogicalBindings: (id: number, revision: string, valueIds: string[]) =>
    logicalPost<void>(`/properties/${id}/dashboard/logical-bindings`, { revision, valueIds }),
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
