import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useSearchParams } from 'react-router'
import { api, ApiError } from '../shared/api'
import { Button, Card, Input } from '../shared/ui'
import { formatMoment, humanize } from '../shared/utils'
import type { DaikinDevice, DaikinRange, Property, Room } from '../shared/types'
import { HistoryChart } from './Energy'

const statusText: Record<string, string> = {
  notConfigured: 'ONECTA developer credentials are required.',
  disconnected: 'Connect your ONECTA account to discover devices.',
  connected: 'ONECTA connected.',
  rateLimited: 'ONECTA request quota is temporarily limited.',
  reauthorizationRequired: 'Reconnect ONECTA to restore access.',
  unavailableForProperty: 'This property has no assigned ONECTA devices.',
  unavailable: 'ONECTA is temporarily unavailable.',
  syncing: 'Synchronizing with ONECTA.',
  waitingForSchema: 'Apply the ONECTA database migration before connecting an account.',
}

const outcomes: Record<string, string> = {
  pending: 'Command pending', failed: 'Command failed', uncertain: 'Outcome uncertain. Awaiting device readback.',
  verified: 'Device state verified', alreadyApplied: 'Device already reports the requested state',
}

const commandErrors: Record<string, string> = {
  readOnly: 'Physical writes are disabled.',
  unsupportedCommand: 'This operation or value is unsupported for the current device and mode.',
  deviceUnavailable: 'This device needs a fresh, online provider observation.',
  unresolvedCommand: 'The previous command still needs a device readback.',
  reauthorizationRequired: 'Reconnect ONECTA before issuing commands.',
  rateLimited: 'The ONECTA request budget is temporarily limited.',
  forbidden: 'This account cannot control the device.',
}

const waterCurves = [
  { metric: 'leavingWaterTemperature', label: 'Leaving water', color: '#11846d' },
  { metric: 'targetLeavingWaterTemperature', label: 'Water target', color: '#5373c7' },
] as const

export function DaikinPanel({ property }: { property: Property }) {
  const cache = useQueryClient()
  const [params, setParams] = useSearchParams()
  const key = ['property', property.id, 'daikin']
  const query = useQuery({ queryKey: key, queryFn: ({ signal }) => api.daikin(property.id, signal), refetchInterval: 30_000 })
  const state = query.data
  const rooms = useQuery({
    queryKey: ['property', property.id, 'rooms'], queryFn: ({ signal }) => api.rooms(property.id, signal),
    enabled: !!state?.manage,
  })
  const action = useMutation({
    mutationFn: async (operation: 'connect' | 'complete' | 'disconnect' | 'refresh') => {
      if (operation === 'connect') {
        const result = await api.connectDaikin(property.id)
        window.location.assign(result.authorizationUrl)
      } else {
        await api.updateDaikin(property.id, operation)
        if (operation === 'complete') setParams({}, { replace: true })
      }
    },
    onSettled: () => cache.invalidateQueries({ queryKey: key }),
  })

  return <section aria-label="Daikin ONECTA">
    <div className="page-heading"><h1>Daikin ONECTA</h1>
      <p>{query.isError ? 'ONECTA status could not be loaded.' : state ? statusText[state.status] ?? humanize(state.status) : 'Loading ONECTA…'}</p>
      {state?.lastSuccess && <p>Last synchronized {formatMoment(state.lastSuccess, property.timeZone)}</p>}
    </div>
    {state?.manage && <div className="filters">
      {params.get('onecta') === 'finish' && <Button onClick={() => action.mutate('complete')} disabled={action.isPending}>Finish connecting</Button>}
      <Button variant="outline" onClick={() => action.mutate('connect')} disabled={action.isPending}>
        {state.status === 'disconnected' ? 'Connect account' : 'Reconnect account'}
      </Button>
      {state.status !== 'disconnected' && <>
        <Button variant="outline" onClick={() => action.mutate('refresh')} disabled={action.isPending}>Refresh discovery</Button>
        <Button variant="outline" onClick={() => action.mutate('disconnect')} disabled={action.isPending}>Disconnect account</Button>
      </>}
      {action.isError && <p role="alert">ONECTA account action failed. Check the connection status.</p>}
    </div>}
    {state?.unassigned.length ? <Card className="panel">
      <h2>Unassigned devices</h2>
      {state.unassigned.map(unit => <div key={unit.id}><p>{unit.name}</p>
        <AssignRoom property={property.id} device={unit} rooms={rooms.data ?? []} /></div>)}
    </Card> : null}
    {state?.devices.length ? <div className="device-grid">
      {state.devices.map(unit => <DaikinDeviceCard key={unit.id} device={unit} property={property}
        writes={state.writes} rooms={state.manage ? rooms.data ?? [] : undefined} />)}
    </div> : null}
  </section>
}

function AssignRoom({ property, device, rooms }: { property: number; device: DaikinDevice; rooms: Room[] }) {
  const [selected, setSelected] = useState(device.roomId?.toString() ?? '')
  const cache = useQueryClient()
  const assignment = useMutation({
    mutationFn: () => api.assignDaikin(property, device.id, selected ? Number(selected) : null),
    onSuccess: () => cache.invalidateQueries({ queryKey: ['property', property, 'daikin'] }),
  })
  return <div className="filters">
    <select className="input" aria-label={`Room for ${device.name}`} value={selected} onChange={event => setSelected(event.target.value)}>
      <option value="">No room</option>{rooms.map(room => <option key={room.id} value={room.id}>{room.name}</option>)}
    </select>
    <Button variant="outline" disabled={assignment.isPending} onClick={() => assignment.mutate()}>
      {assignment.isPending ? 'Saving…' : 'Assign here'}
    </Button>
    {assignment.isError && <p role="alert">Could not assign this device.</p>}
  </div>
}

export function DaikinDeviceCard({ device, property, writes, rooms }: {
  device: DaikinDevice; property: Property; writes: boolean; rooms?: Room[]
}) {
  const cache = useQueryClient()
  const command = useMutation({
    mutationFn: ({ operation, value }: { operation: string; value: string }) => api.commandDaikin(property.id, device.id, operation, value),
    onSettled: () => cache.invalidateQueries({ queryKey: ['property', property.id, 'daikin'] }),
  })
  const effective = command.data && (!device.command || Date.parse(command.data.updatedAt) > Date.parse(device.command.updatedAt))
    && !(device.command?.id === command.data.id && device.command.status === 'verified') ? command.data : device.command
  const heatPump = device.equipment === 'heatPump'
  const supportedAc = device.equipment === 'airConditioner'
  const blocked = !writes || !supportedAc || device.quality !== 'fresh' || device.connectivity !== 'online' || !device.sourceAt ||
    command.isPending || effective?.status === 'pending' || effective?.status === 'uncertain'
  const reading = (value: number | null, units: string) => value === null ? 'Unavailable' : `${value} ${units}`
  const state = (value: boolean | null | undefined) => value == null ? 'Unavailable' : value ? 'Active' : 'Clear'
  const modeName = (value: string | null | undefined) => value ? humanize(value) : 'Unavailable'
  const water = device.water
  const fields = [
    ['Power', device.power ? humanize(device.power) : 'Unavailable'],
    ['Operating mode', device.mode ? humanize(device.mode) : 'Unavailable'],
    ...(heatPump ? [
      ['Leaving water', reading(water?.leaving ?? null, '°C')],
      ['Water target', reading(water?.target ?? null, '°C')],
      ['Outdoor temperature', reading(device.outdoor, '°C')],
      ['Heating control', modeName(water?.controlMode)],
      ['Setpoint mode', modeName(water?.setpointMode)],
      ['Warning', state(water?.warning)], ['Emergency', state(water?.emergency)],
    ] : [
      ['Indoor temperature', reading(device.indoor, '°C')], ['Target temperature', reading(device.target, '°C')],
      ...(device.outdoor === null ? [] : [['Outdoor temperature', reading(device.outdoor, '°C')]]),
      ...(device.humidity === null ? [] : [['Humidity', reading(device.humidity, '%')]]),
      ...(device.fanMode ? [['Fan', [humanize(device.fanMode), device.fan].filter(value => value !== null).join(' · ')]] : []),
    ]),
  ]
  function submit(operation: string, value: string) { command.mutate({ operation, value }) }
  return <Card className="device-card">
    <h2>{device.name}</h2>
    {device.model && <p>{device.model}</p>}
    <p>{humanize(device.connectivity)} · {humanize(device.quality)}</p>
    <p>{device.sourceAt ? 'Source observation' : 'Cloud snapshot received'} {formatMoment(device.observedAt, property.timeZone)}</p>
    {device.fault && <p>Fault {device.fault}</p>}
    <dl className="device-readings">{fields.map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}</dl>
    {heatPump ? <><p>Read-only heat-pump monitoring</p><WaterHistory device={device} property={property} /></>
      : supportedAc ? <>
        {!writes && <p>Read-only monitoring · physical writes disabled or unauthorized</p>}
        {writes && blocked && <p>Commands are unavailable until the device has fresh online data and any pending result is resolved.</p>}
        <Choice label="Power" choices={device.controls.power} selected={device.power} disabled={blocked} change={value => submit('power', value)} />
        <Choice label="Mode" choices={device.controls.modes} selected={device.mode} disabled={blocked} change={value => submit('mode', value)} />
        <TemperatureControl range={device.controls.temperature} current={device.target} disabled={blocked}
          change={value => submit('temperature', value)} />
        <NumberControl label="Fixed fan speed command" range={device.controls.fan} disabled={blocked} change={value => submit('fan', value)} />
      </> : <p>Read-only monitoring</p>}
    {command.isPending && <p role="status">Command pending</p>}
    {command.isError && <p role="alert">{command.error instanceof ApiError && command.error.code &&
      Object.hasOwn(commandErrors, command.error.code) ? commandErrors[command.error.code]
      : 'Command could not be confirmed. Check the device and command status.'}</p>}
    {effective && <p role="status">{outcomes[effective.status] ?? humanize(effective.status)} · {humanize(effective.operation)} {effective.value}</p>}
    {rooms && <AssignRoom property={property.id} device={device} rooms={rooms} />}
  </Card>
}

function Choice({ label, choices, selected, disabled, change }: {
  label: string; choices: string[]; selected: string | null; disabled: boolean; change: (value: string) => void
}) {
  if (!choices.length) return null
  return <div className="filters"><span>{label} controls</span>{choices.map(value =>
    <Button key={value} variant="outline" disabled={disabled || value === selected} onClick={() => change(value)}>{humanize(value)}</Button>)}</div>
}

function TemperatureControl({ range, current, disabled, change }: {
  range: DaikinRange | null; current: number | null; disabled: boolean; change: (value: string) => void
}) {
  if (!range) return null
  const lower = current === null ? null : Number((current - range.step).toFixed(8))
  const higher = current === null ? null : Number((current + range.step).toFixed(8))
  const allowed = (value: number | null) => value !== null && range.step > 0 && value >= range.min && value <= range.max &&
    Math.abs((value - range.min) / range.step - Math.round((value - range.min) / range.step)) < 1e-7
  return <div className="filters">
    <span>Target controls · {range.min}–{range.max} °C · {range.step} °C step</span>
    <Button aria-label="Lower target temperature" variant="outline" disabled={disabled || !allowed(lower)}
      onClick={() => change(String(lower))}>−</Button>
    <Button aria-label="Raise target temperature" variant="outline" disabled={disabled || !allowed(higher)}
      onClick={() => change(String(higher))}>+</Button>
  </div>
}

function WaterHistory({ device, property }: { device: DaikinDevice; property: Property }) {
  const [open, setOpen] = useState(false)
  const history = useQuery({
    queryKey: ['property', property.id, 'daikin-water-history', device.id],
    queryFn: ({ signal }) => api.daikinWaterHistory(property.id, device.id, signal),
    enabled: open,
    refetchInterval: 60_000,
  })
  return <>
    <Button variant="outline" aria-expanded={open} onClick={() => setOpen(!open)}>
      {open ? 'Hide water history' : 'Show water history'}
    </Button>
    {open && <HistoryChart property={property} title="Water temperatures" curves={[...waterCurves]} hours={24} unit="°C"
      pollSeconds={history.data?.pollSeconds ?? 900} minimumPoints={2} series={history.data?.series ?? []}
      pending={history.isPending} failed={history.isError} />}
  </>
}

function NumberControl({ label, range, disabled, change }: {
  label: string; range: DaikinRange | null; disabled: boolean; change: (value: string) => void
}) {
  if (!range) return null
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    change(String(new FormData(event.currentTarget).get('value')))
  }
  return <form className="filters" onSubmit={submit}>
    <Input name="value" aria-label={label} type="number" min={range.min} max={range.max} step={range.step} required disabled={disabled} />
    <Button variant="outline" type="submit" disabled={disabled}>Set {label.toLowerCase().replace(' command', '')}</Button>
  </form>
}
