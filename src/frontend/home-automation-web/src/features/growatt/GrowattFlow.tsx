// The Growatt power-flow view (prompt 0069). It renders the same Growatt state as the Details view and reuses its summary
// (`solar`), PV generation state and device availability; it adds no request and no estimate of its own.
import type { Device, GrowattState, InstallationValue, Reading } from '../../shared/types'
import { Badge, Card } from '../../shared/ui'
import { formatReading, metricLabels } from '../Devices'
import { pvStatus } from '../GrowattPanel'
import { batteryFlow, cloudOffline, dailyTotal, dataStatus, inverters, readingOf, solarShare } from './flow'
import { Battery, GridTower, House, InverterBox, SolarPanel } from './illustrations'

type Tone = 'solar' | 'grid' | 'battery' | 'home'
type Direction = 'forward' | 'reverse' | null

const watts = new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 })
const decimal = new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 })
const W = (value: number) => `${watts.format(value)} W`

const fresh = (reading: Reading | undefined) => reading?.quality === 'fresh' && reading.number !== null ? reading.number : null
const slot = (values: InstallationValue[] | null | undefined, name: InstallationValue['slot']) => values?.find(item => item.slot === name)
const flowing = (value: InstallationValue | undefined) =>
  !!value && (value.state === 'available' || value.state === 'partial') && (value.number ?? 0) > 0

/** Whether any inverter is producing PV, by the same rule as the Details view's PV badge (including "No PV generation (0 V)"). */
function pvGenerating(device: Device) {
  return device.endpoints.some(endpoint => {
    const power = endpoint.measurements.find(reading => reading.metric === 'pvPower')
    if (!power) return false
    const voltage = [endpoint.measurements.find(reading => reading.metric === 'pv1Voltage'),
      endpoint.measurements.find(reading => reading.metric === 'pvVoltage')]
      .find(reading => reading?.quality === 'fresh' && reading.number !== null)
    return pvStatus(device.pvInputs?.find(item => item.endpointId === endpoint.id), power, voltage).text === 'PV generating'
  })
}

export function GrowattFlow({ result, timeZone, message }: { result?: GrowattState; timeZone: string; message?: string }) {
  const state = result?.data
  if (!state) return <Card className="panel"><p>{message}</p></Card>
  const list = inverters(state.devices)
  const battery = state.devices.find(device => device.kind === 'battery')
  const pv = slot(state.solar, 'pvPower')
  const output = slot(state.solar, 'consumption')
  const supply = slot(state.solar, 'gridSupply')
  const acInput = slot(state.solar, 'acInput')
  const generating = list.some(pvGenerating)
  const flow = batteryFlow(state.devices)
  const soc = fresh(readingOf(battery, 'batterySoc')) ?? readingOf(battery, 'batterySoc')?.number ?? null
  const batteryVolts = readingOf(battery, 'voltage')?.number ?? null
  const frequency = list.map(device => fresh(readingOf(device, 'gridFrequency'))).find(value => value !== null) ?? null
  const mode = list.map(device => readingOf(device, 'operatingMode')).find(reading => reading?.code)?.code ?? null
  const status = dataStatus(state.devices)
  const time = (at: string) => new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit', hourCycle: 'h23', timeZone }).format(Date.parse(at))
  const solarWatts = generating && pv?.number !== null && pv?.number !== undefined ? pv.number : null
  const batteryDirection: Direction = flow.state === 'charging' ? 'forward' : flow.state === 'discharging' ? 'reverse' : null

  return <section className="growatt-flow" aria-label="Поток на енергията">
    <div className="flow-status">
      <Badge className={`tone-${status.tone}`}><i />{status.label === 'none' ? 'Няма данни'
        : `${status.label === 'receiving' ? 'Получава данни' : 'Остарели данни'}${status.at ? ` · ${time(status.at)}` : ''}`}</Badge>
      {cloudOffline(state.devices) && <span className="flow-cloud" title={cloudNote}>Облак: offline<span className="sr-only">. {cloudNote}</span></span>}
    </div>

    <Card className="flow-diagram-card">
      <div className="flow-diagram">
        <FlowNode area="solar" title="Солар" active={generating} illustration={<SolarPanel active={generating} />}
          lines={[solarWatts !== null ? `${W(solarWatts)}${pv?.state === 'partial' ? ' (частично)' : ''}` : 'Няма производство']} />
        <FlowLine area="sline" axis="v" tone="solar" direction={generating && (solarWatts ?? 0) > 0 ? 'forward' : null}
          value={solarWatts} label="Солар към инвертора" />

        <FlowNode area="grid" title="Мрежа" active={supply?.text === 'present'} illustration={<GridTower active={supply?.text === 'present'} />}
          lines={[acInput?.number !== null && acInput?.number !== undefined ? `Внос: ${W(acInput.number)}` : 'Внос: —',
            [supply?.text === 'absent' ? 'Няма напрежение' : supply?.number != null ? `${decimal.format(supply.number)} V` : null,
              frequency !== null ? `${decimal.format(frequency)} Hz` : null].filter(Boolean).join(' · ') || null]} />
        <FlowLine area="gline" axis="h" tone="grid" direction={flowing(acInput) ? 'forward' : null} value={acInput?.number ?? null}
          label="Мрежа към инвертора" />

        <FlowNode area="inv" title="Инвертор" active={status.tone === 'good'} illustration={<InverterBox active={status.tone === 'good'} />}
          lines={[mode, list.map(device => {
            const load = readingOf(device, 'inverterLoadPercent')
            return `${shortName(device.name)} ${load?.number != null ? `${decimal.format(load.number)} %` : '—'}`
          }).join(' · ') || null]} />

        <FlowLine area="hline" axis="h" tone="home" direction={flowing(output) ? 'forward' : null} value={output?.number ?? null}
          label="Инвертор към дома" />
        <FlowNode area="home" title="Дом" active={flowing(output)} illustration={<House active={flowing(output)} />}
          lines={[output?.number != null ? `${W(output.number)}${output.state === 'partial' ? ' (частично)' : ''}` : '—']} />

        <FlowLine area="bline" axis="v" tone="battery" direction={batteryDirection} value={flow.net === null ? null : Math.abs(flow.net)}
          label={flow.state === 'discharging' ? 'Батерия към инвертора' : 'Инвертор към батерията'} />
        <FlowNode area="batt" title="Батерия" active={flow.state !== 'unavailable'}
          illustration={<Battery level={soc} charging={flow.state === 'charging'} />}
          lines={[[soc !== null ? `${decimal.format(soc)} %` : null, batteryVolts !== null ? `${decimal.format(batteryVolts)} V` : null]
            .filter(Boolean).join(' · ') || '—',
          flow.state === 'charging' ? `Зарежда ${W(flow.net!)}` : flow.state === 'discharging' ? `Разрежда ${W(-flow.net!)}`
            : flow.state === 'idle' ? 'В покой' : 'Потокът не е наличен']}
          note={flow.state === 'unavailable' ? null : flow.used < flow.expected
            ? `по данни от ${flow.used} от ${flow.expected} инвертора` : 'по данни от инверторите'} />
      </div>
    </Card>

    <TodayRow devices={state.devices} />
    <div className="flow-inverters">{list.map(device => <InverterCard key={device.id} device={device} />)}</div>
  </section>
}

const cloudNote = 'Облакът на Growatt маркира поне един инвертор като offline (флаг „lost“). Статусът вляво следва последните ' +
  'отчитания от инверторите, затова този етикет е само за информация.'

const shortName = (name: string) => name.replace(/^Growatt\s+/i, '').replace(/^./, character => character.toUpperCase())

function FlowNode({ area, title, illustration, lines, active, note }: {
  area: string; title: string; illustration: React.ReactNode; lines: (string | null)[]; active: boolean; note?: string | null
}) {
  return <div className={`flow-node${active ? ' active' : ''}`} style={{ gridArea: area }}>
    <div className="flow-medallion">{illustration}</div>
    <div className="flow-node-text">
      <strong>{title}</strong>
      {lines.filter((line): line is string => !!line).map(line => <span key={line}>{line}</span>)}
      {note && <small>{note}</small>}
    </div>
  </div>
}

/** A connector: animated chevrons in the flow direction, coloured by source, or a static dashed grey line without flow. */
function FlowLine({ area, axis, tone, direction, value, label }: {
  area: string; axis: 'h' | 'v'; tone: Tone; direction: Direction; value: number | null; label: string
}) {
  const text = direction && value !== null ? W(value) : null
  return <div className={`flow-line flow-${tone}${direction ? ' flowing' : ''}`} data-axis={axis} data-direction={direction ?? 'none'}
    style={{ gridArea: area }}>
    <span className="sr-only">{label}: {text ?? 'няма поток'}</span>
    <span className="flow-track" aria-hidden="true">
      {direction && <span className="flow-chevrons">{[0, 1, 2].map(index =>
        <svg key={index} className="flow-chevron" viewBox="0 0 10 12" style={{ animationDelay: `${(direction === 'reverse' ? 2 - index : index) * 0.3}s` }}>
          <path d="M2.5 1.5 L7.5 6 L2.5 10.5" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" />
        </svg>)}</span>}
    </span>
    {text && <span className="flow-value" aria-hidden="true">{text}</span>}
  </div>
}

const kWh = (value: number | null) => value === null ? '—' : `${decimal.format(value)} kWh`

function TodayRow({ devices }: { devices: Device[] }) {
  const pv = dailyTotal(devices, 'pvEnergyToday')
  const consumption = dailyTotal(devices, 'outputEnergyToday')
  const discharged = dailyTotal(devices, 'batteryDischargedEnergyToday')
  const share = solarShare(pv.total, consumption.total)
  const parts = (total: ReturnType<typeof dailyTotal>) => total.parts.map(part => `${shortName(part.name)} ${kWh(part.value)}`).join(' + ')
  return <section className="flow-today" aria-label="Днес">
    <h3>Днес</h3>
    <div className="flow-today-grid">
      {([['Произведено от солар', kWh(pv.total), parts(pv), pv.partial], ['Консумация', kWh(consumption.total), parts(consumption), consumption.partial],
        ['Разредено от батерия', kWh(discharged.total), parts(discharged), discharged.partial],
        ['Дял на солара', share === null ? '—' : `${decimal.format(share)} %`, 'от консумацията (PV ÷ изход, най-много 100 %)', false]] as const)
        .map(([label, value, detail, partial]) => <Card key={label} className="flow-today-card">
          <span>{label}</span><strong>{value}{partial ? ' (частично)' : ''}</strong><small>{detail}</small>
        </Card>)}
    </div>
  </section>
}

const statusText = { online: 'Онлайн', stale: 'Остарели данни', offline: 'Офлайн', unknown: 'Неизвестно' } as const
const statusTone = { online: 'good', stale: 'warn', offline: 'neutral', unknown: 'neutral' } as const
const mainMetrics = ['activePower', 'acInputPower', 'batteryPower', 'inverterLoadPercent'] as const

function valueOf(reading: Reading | undefined) {
  if (!reading || reading.quality === 'missing') return '—'
  return `${formatReading(reading)}${reading.quality === 'stale' ? ' (остаряло)' : ''}`
}

function batteryText(reading: Reading | undefined) {
  if (!reading || reading.quality === 'missing' || reading.number === null) return '—'
  const text = reading.number > 0 ? `Зарежда ${W(reading.number)}` : reading.number < 0 ? `Разрежда ${W(-reading.number)}` : 'В покой'
  return `${text}${reading.quality === 'stale' ? ' (остаряло)' : ''}`
}

function InverterCard({ device }: { device: Device }) {
  const availability = device.availability ?? (device.connectivity === 'offline' ? 'offline' : device.quality === 'stale' ? 'stale' : device.connectivity)
  const load = readingOf(device, 'inverterLoadPercent')
  const percent = load?.number != null ? Math.min(100, Math.max(0, load.number)) : null
  const others = device.endpoints.flatMap(endpoint => endpoint.measurements.map(reading => ({ endpoint, reading })))
    .filter(({ reading }) => !mainMetrics.some(metric => metric === reading.metric))
  return <Card className="flow-inverter" aria-label={device.name}>
    <div className="flow-inverter-head"><h3>{device.name}</h3>
      <Badge className={`tone-${device.faultCode ? 'bad' : statusTone[availability]}`}><i />{device.faultCode ? 'Грешка' : statusText[availability]}</Badge>
    </div>
    <dl className="flow-inverter-numbers">
      <div><dt>Товар</dt><dd>{valueOf(readingOf(device, 'activePower'))}</dd></div>
      <div><dt>Внос от мрежата</dt><dd>{valueOf(readingOf(device, 'acInputPower'))}</dd></div>
      <div><dt>Батерия</dt><dd>{batteryText(readingOf(device, 'batteryPower'))}</dd></div>
    </dl>
    <div className="flow-load">
      <span>Натоварване {percent === null ? '—' : `${decimal.format(percent)} %`}</span>
      <div className="flow-load-bar" role="meter" aria-label={`Натоварване на ${device.name}`} aria-valuemin={0} aria-valuemax={100}
        aria-valuenow={percent ?? undefined} aria-valuetext={percent === null ? 'няма данни' : `${decimal.format(percent)} %`}>
        <span style={{ width: `${percent ?? 0}%` }} />
      </div>
    </div>
    {others.length > 0 && <details className="flow-all">
      <summary>Всички показания</summary>
      <dl className="device-readings">{others.map(({ endpoint, reading }) => <div key={`${endpoint.id}:${reading.metric}`}>
        <dt>{metricLabels[reading.metric]}</dt><dd>{valueOf(reading)}</dd></div>)}</dl>
    </details>}
  </Card>
}
