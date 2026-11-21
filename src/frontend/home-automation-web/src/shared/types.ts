export type Quality = 'fresh' | 'stale' | 'missing'
export type Metric =
  | 'pvPower' | 'householdPower' | 'gridAvailable' | 'batterySoc' | 'batteryPower'
  | 'voltage' | 'current' | 'activePower' | 'energy' | 'temperature'
  | 'humidity' | 'switchOn' | 'targetTemperature' | 'operatingMode'
  | 'gridVoltage' | 'gridFrequency' | 'gridImportPower' | 'pvVoltage' | 'warningStatus'
  | 'acOutputVoltage' | 'inverterLoadPercent' | 'acChargingPower'

export type Property = { id: number; name: string; timeZone: string }
export type Room = { id: number; name: string }
export type Reading = {
  metric: Metric
  unit: string | null
  observedAt: string | null
  number: number | null
  flag: boolean | null
  code: string | null
  quality: Quality
}

export type Device = {
  id: number
  roomId: number | null
  name: string
  kind: string
  manufacturer: string
  model: string
  connectivity: 'online' | 'offline' | 'unknown'
  faultCode: string | null
  lastContactAt: string | null
  quality: Quality
  endpoints: {
    id: number
    name: string
    capabilities: { code: string; access: 'readOnly' | 'unsupported' }[]
    measurements: Reading[]
  }[]
}

export type PropertyState = {
  devices: Device[]
  installation: Reading[]
  alerts: { deviceId: number; deviceName: string; code: string; occurredAt: string }[]
}

export type Point = Omit<Reading, 'metric' | 'unit' | 'quality'> & { observedAt: string }
export type Health = { database: string; simulator: string; lastSuccess: string | null; pollSeconds: number }
export type GrowattState = { status: string; lastSuccess: string | null; data: PropertyState | null }
