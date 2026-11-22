export type Quality = 'fresh' | 'stale' | 'missing'
export type Metric =
  | 'pvPower' | 'householdPower' | 'gridAvailable' | 'batterySoc' | 'batteryPower'
  | 'voltage' | 'current' | 'activePower' | 'energy' | 'temperature'
  | 'humidity' | 'switchOn' | 'targetTemperature' | 'operatingMode'
  | 'gridVoltage' | 'gridFrequency' | 'gridImportPower' | 'pvVoltage' | 'warningStatus'
  | 'acOutputVoltage' | 'inverterLoadPercent' | 'acChargingPower'
  | 'outdoorTemperature' | 'fanSpeed'
  | 'leavingWaterTemperature' | 'targetLeavingWaterTemperature' | 'heatingControlMode' | 'waterSetpointMode'
  | 'heatingWarning' | 'heatingEmergency'

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

export type DaikinRange = { min: number; max: number; step: number }
export type DaikinOutcome = { id: string; operation: string; value: string; status: string; updatedAt: string }
export type DaikinDevice = {
  id: string; name: string; model: string; roomId: number | null; connectivity: Device['connectivity']
  observedAt: string; sourceAt: string | null; quality: Quality
  power: string | null; mode: string | null; indoor: number | null; outdoor: number | null; target: number | null
  humidity: number | null; fan: number | null; fanMode: string | null; fault: string | null
  controls: { power: string[]; modes: string[]; temperature: DaikinRange | null; fan: DaikinRange | null }
  command: DaikinOutcome | null
  equipment: 'unknown' | 'airConditioner' | 'heatPump'
  water: {
    leaving: number | null; target: number | null; targetRange: DaikinRange | null
    controlMode: string | null; setpointMode: string | null; warning: boolean | null; emergency: boolean | null
    providerUnit: string | null; targetAdvertisedWritable: boolean | null
  } | null
}
export type DaikinWaterHistory = { pollSeconds: number; series: { metric: Metric; points: Point[] }[] }
export type DaikinState = {
  status: string; lastSuccess: string | null; manage: boolean; writes: boolean
  devices: DaikinDevice[]; unassigned: DaikinDevice[]
}
