export type Quality = 'fresh' | 'stale' | 'missing'
export type Metric =
  | 'pvPower' | 'householdPower' | 'gridAvailable' | 'batterySoc' | 'batteryPower'
  | 'voltage' | 'current' | 'activePower' | 'energy' | 'temperature'
  | 'humidity' | 'switchOn' | 'targetTemperature' | 'operatingMode'
  | 'gridVoltage' | 'gridFrequency' | 'gridImportPower' | 'pvVoltage' | 'pv1Voltage' | 'warningStatus'
  | 'acOutputVoltage' | 'inverterLoadPercent' | 'acChargingPower' | 'acInputPower'
  | 'pvEnergyToday' | 'outputEnergyToday' | 'batteryDischargedEnergyToday'
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

export type EwelinkRelayOutcome = { id: string; status: 'pending' | 'confirmed' | 'failed' | 'unverified'; open: boolean }
export type EwelinkDevice = {
  id: string; name: string; model: string; roomId: number | null; connectivity: Device['connectivity']
  seenAt: string; quality: Quality; hasRelay: boolean; relay: boolean | null; relayAt: string | null; relayQuality: Quality
  measurements?: Reading[]
  supportedMetrics?: Metric[]
  socketMetrics?: Metric[]
  serverReceivedMetrics?: Metric[]
  observedRelay?: boolean | null; observedRelayAt?: string | null; observedRelayQuality?: Quality
  electricalReporting?: { status: 'waitingForActivation' | 'awaitingReport' | 'receivingReports' | 'activationRejected' | 'activationUnconfirmed' | 'scheduledCollection' | 'deviceUnavailable'; providerCode?: number | null } | null
  canWriteRelay?: boolean; requiresRelayConfirmation?: boolean; relayCommand?: EwelinkRelayOutcome | null
  relayControlStatus?: 'enabled' | 'deviceDisabled' | 'globalDisabled' | 'awaitingDiscovery' | 'unsupported' | null
}
export type EwelinkState = {
  status: string; manage: boolean; lastSuccess: string | null; nextSync: string | null; lastAttempt?: string | null
  devices: EwelinkDevice[]; unassigned: EwelinkDevice[]
  canViewLiveElectrical?: boolean
}

export type TuyaReading = {
  code: string; metric: Metric; unit: string | null; number: number | null; flag: boolean | null
  reportedAt: string | null; quality: Quality
}
export type TuyaDevice = {
  id: string; name: string; model: string; roomId: number | null; connectivity: Device['connectivity']
  receivedAt: string; quality: Quality; specificationsAvailable: boolean; readings: TuyaReading[]
}
export type TuyaState = {
  status: string; manage: boolean; lastSuccess: string | null; nextSync: string | null; providerCode: number | null
  devices: TuyaDevice[]; unassigned: TuyaDevice[]
}

export type DevicePageKey = 'dashboard' | 'devices' | 'growatt' | 'daikin' | 'ewelink' | 'tuya'
export type DevicePreferences = { order: Record<string, string[]>; hidden: string[] }
export type SensorPeriod = '24h' | '7d' | '30d'
export type SensorHistory = {
  from: string; to: string; pollSeconds: number
  series: { code: string; metric: 'temperature' | 'humidity'; unit: string; points: Point[]; truncated: boolean }[]
}

export type DaikinRange = { min: number; max: number; step: number }
export type DaikinOutcome = { id: string; operation: string; value: string; status: string; updatedAt: string }
export type DaikinDevice = {
  id: string; name: string; model: string; roomId: number | null; connectivity: Device['connectivity']
  observedAt: string; sourceAt: string | null; quality: Quality; providerReceivedAt?: string | null
  power: string | null; mode: string | null; indoor: number | null; outdoor: number | null; target: number | null
  humidity: number | null; fan: number | null; fanMode: string | null; fault: string | null
  controls: { power: string[]; modes: string[]; temperature: DaikinRange | null; fan: DaikinRange | null }
  command: DaikinOutcome | null
  openCommands?: DaikinOutcome[]
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
export type DashboardValue = {
  id: string; sourceKey: string; label: string | null; colorMode?: 'automatic' | 'fixed'; customColor?: string | null
}
export type DashboardWidget = {
  id: string; title: string; mode: 'compact' | 'detailed'; navigationSource: string | null; values: DashboardValue[]
  width?: number; measurementColumns?: number
}
export type DashboardGroup = { id: string; title: string; hidden: boolean; collapsed: boolean; widgets: DashboardWidget[] }
export type DashboardReading = {
  number: number | null; flag: boolean | null; code: string | null; observedAt: string
  provenance: 'provider' | 'serverReceived' | 'simulated'
}
export type DashboardSource = {
  key: string; provider: 'growatt' | 'daikin' | 'ewelink' | 'tuya' | 'simulator' | 'logical'
  deviceId: string; deviceName: string; channel: string | null; metric: Metric; unit: string | null
  route: string; reading: DashboardReading | null
  status?: 'unsupported' | 'unassigned' | 'ambiguous' | 'archived' | null; physicalProvider?: string | null
}
export type LogicalAssignment = {
  id: number; channelKey: string; deviceId: number; deviceName: string; provider: string; manufacturer: string; model: string
  endpointId: number | null; endpointName: string | null; validFrom: string; validTo: string | null; assignedBy: string
  assignedAt: string; reason: string | null; capabilities: string[]
}
export type LogicalDevice = {
  id: number; name: string; kind: string; roomId: number | null; description: string | null; createdAt: string; updatedAt: string
  archivedAt: string | null; current: LogicalAssignment[]; previous: LogicalAssignment[]; metrics?: LogicalMetric[] | null
  creation?: LogicalCreation
}
export type LogicalDeviceList = { canManage: boolean; devices: LogicalDevice[] }
export type MetricAggregation = 'gauge' | 'boolean' | 'status' | 'dailyResetCounter' | 'cumulativeCounter'
export type LogicalMetric = { channelKey: string; metric: Metric; unit: string | null; aggregation: MetricAggregation; current: boolean }
export type HistoryQuality = 'complete' | 'incomplete' | 'noData' | 'notAggregated'
export type HistoryEnergy = {
  status: 'measured' | 'estimated' | 'incomplete' | 'noData'; basis: 'counter' | 'powerIntegration'
  wh: number | null; positiveWh: number | null; negativeWh: number | null
}
export type HistoryBucket = {
  start: string; end: string; date: string | null; durationSeconds: number; quality: HistoryQuality; coverage: number
  coveredSeconds: number; sampleCount: number; minimum: number | null; maximum: number | null; average: number | null
  mean: number | null; firstObservedAt: string | null; lastObservedAt: string | null; lastValue: number | null
  onSeconds: number | null; transitions: number | null; energy: HistoryEnergy | null; provisional: boolean; flags: string[]
  seriesIds?: number[] | null
}
export type LogicalHistorySegment = {
  assignmentId: number; deviceId: number; deviceName: string; endpointId: number | null; from: string; to: string | null
  status: 'available' | 'unsupported' | 'ambiguous'; seriesId: number | null; source: string | null
  alternativeSeriesIds: number[]; maximumHoldSeconds?: number | null
}
export type LogicalRawPoint = {
  observedAt: string; receivedAt: string; number: number | null; flag: boolean | null; code: string | null
  timestampSource: string; seriesId: number; assignmentId: number
}
export type LogicalHistory = {
  logicalDeviceId: number; name: string; channelKey: string; metric: Metric; unit: string | null; aggregation: MetricAggregation
  resolution: 'raw' | 'hour' | 'day'; from: string; to: string; timeZone: string; incompleteBelowCoverage: number
  segments: LogicalHistorySegment[]; points: LogicalRawPoint[] | null; buckets: HistoryBucket[] | null; nextAfter: string | null
}
export type ReplacementCandidate = {
  deviceId: number; name: string; provider: string; manufacturer: string; model: string; kind: string; roomId: number | null
  endpoints: { id: number; name: string; capabilities: string[] }[]; capabilities: string[]; shared: string[]; missing: string[]
  added: string[]; compatible: boolean; requiresEndpoint: boolean; assignedLogicalDeviceId: number | null
  assignedLogicalDeviceName: string | null; current: boolean; assignedLogicalCreation?: LogicalCreation | null
}
export type ReplaceDeviceRequest = {
  channelKey: string; expectedAssignmentId: number; deviceId: number; endpointId: number | null; effectiveAt: string
  reason: string | null; acceptMissingCapabilities: boolean
} & ReleaseOptions
/** Explicitly ends another logical device's use of the hardware at the effective time. */
export type ReleaseOptions = { releaseFrom?: number | null; acceptReleasedHistory?: boolean; archiveReleased?: boolean }
export type AssignDeviceRequest = {
  channelKey: string; deviceId: number; endpointId: number | null; effectiveAt: string | null; reason: string | null
} & ReleaseOptions
export type LogicalCreation = 'manual' | 'initialized' | 'automatic'
export type LogicalDeviceInput = { name: string; kind: string; roomId: number | null; description: string | null }
export type PhysicalDevice = {
  id: number; name: string; provider: string; manufacturer: string; model: string; kind: string; roomId: number | null
  capabilities: string[]; endpoints: { id: number; name: string; capabilities: string[] }[]; requiresEndpoint: boolean
  logicalDeviceId: number | null; logicalDeviceName: string | null; logicalCreation: LogicalCreation | null; everAssigned: boolean
}
export type PhysicalDeviceList = { canManage: boolean; devices: PhysicalDevice[] }
export type ReleasePreview = {
  logicalDeviceId: number; name: string; creation: LogicalCreation; channels: string[]; readings: number
  firstReading: string | null; lastReading: string | null; leavesUnassigned: boolean; hasOtherHardwareHistory: boolean
  dashboardReferences: number
}
export type LogicalBindingReview = {
  revision: string
  proposals: { valueId: string; widgetTitle: string; sourceKey: string; logicalKey: string; logicalName: string; metric: Metric }[]
  skipped: { valueId: string; sourceKey: string; reason: string }[]
}
export type DashboardSnapshot = { revision: string; canEdit: boolean; groups: DashboardGroup[]; sources: DashboardSource[] }
export type ProviderConfigurationView = {
  provider: string; revision: number; pollSeconds: number; minimumPollSeconds: number
  maximumPollSeconds: number; pollStepSeconds: number; credentials: Record<string, boolean>
  identifiers: Record<string, string | null>; pendingIdentifierConversion: string[]
  /** Daikin, eWeLink and Tuya use one installation-wide account configured in the global scope. */
  installationWide?: boolean
  /** Presence of the global profile, reported on property views only. Values are never included. */
  global?: { revision: number; credentials: Record<string, boolean>; identifiers: Record<string, boolean> } | null
}
export type ConfigurationView = {
  canEdit: boolean; keyAvailable: boolean; requiresHttps?: boolean; providers: ProviderConfigurationView[]
}
export type ProviderConfigurationUpdate = {
  revision: number; pollSeconds: number; replacements: Record<string, string>; remove: string[]
  identifiers?: Record<string, string>
}
