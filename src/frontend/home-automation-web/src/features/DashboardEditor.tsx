import { useMemo, useRef, useState, type FormEvent, type PointerEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowDown, ArrowUp, GripVertical, Plus, Trash2 } from 'lucide-react'
import { api, ApiError } from '../shared/api'
import type { DashboardGroup, DashboardSnapshot, DashboardSource, DashboardValue, DashboardWidget, Property } from '../shared/types'
import { Button, Input } from '../shared/ui'
import { metricLabels } from './Devices'
import { MonitoringWidget, providerNames, useDashboardTime } from './DashboardWidgets'

export const dashboardKey = (id: number) => ['property', id, 'dashboard'] as const
const emptySources: DashboardSource[] = []
const newId = () => crypto.randomUUID()
const newValue = (): DashboardValue => ({ id: newId(), sourceKey: '', label: null, colorMode: 'automatic', customColor: null })
const newWidget = (): DashboardWidget => ({ id: newId(), title: '', mode: 'compact', navigationSource: null,
  width: 1, measurementColumns: 2, values: [newValue()] })
const newGroup = (): DashboardGroup => ({ id: newId(), title: '', hidden: false, collapsed: true, widgets: [] })
const change = <T,>(items: T[], index: number, item: T) => items.map((existing, position) => position === index ? item : existing)
const remove = <T,>(items: T[], index: number) => items.filter((_, position) => position !== index)
function move<T>(items: T[], index: number, direction: number) {
  const result = [...items]
  const [item] = result.splice(index, 1)
  result.splice(index + direction, 0, item)
  return result
}

const columns = [1, 2, 3, 4]
const colorPresets = {
  '#007f36': 'Green', '#006aca': 'Blue', '#985006': 'Orange',
  '#b91c1c': 'Red', '#6d28d9': 'Purple', '#666666': 'Gray',
}

export function DashboardEditor({ property, snapshot, done }: { property: Property; snapshot: DashboardSnapshot; done: () => void }) {
  const [groups, setGroups] = useState(snapshot.groups)
  const [revision] = useState(snapshot.revision)
  const [validation, setValidation] = useState('')
  const client = useQueryClient()
  const catalog = useQuery({
    queryKey: ['property', property.id, 'dashboard-sources'],
    queryFn: ({ signal }) => api.dashboardSources(property.id, signal),
  })
  const sources = catalog.data ?? emptySources
  const previewSources = useMemo(() => new Map([...sources, ...snapshot.sources].map(source => [source.key, source])), [sources, snapshot.sources])
  const now = useDashboardTime(snapshot.sources.length ? snapshot.sources : sources)
  const saving = useMutation({
    mutationFn: () => api.saveDashboard(property.id, revision, groups),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: dashboardKey(property.id), exact: true })
      done()
    },
  })
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setValidation('')
    const widgets = groups.flatMap(group => group.widgets)
    if (groups.some(group => !group.title.trim()) || widgets.some(widget => !widget.title.trim())) {
      setValidation('Enter a title for every group and widget.')
    } else if (widgets.some(widget => !widget.values.length || widget.values.some(value => !value.sourceKey))) {
      setValidation('Choose at least one measurement for every widget and complete each source selection.')
    } else if (widgets.reduce((count, widget) => count + widget.values.length, 0) > 512) {
      setValidation('A dashboard can contain up to 512 measurements.')
    } else saving.mutate()
  }
  const error = saving.error instanceof ApiError ? saving.error : undefined
  const saveError = error?.status === 409
    ? 'Someone changed this dashboard. Cancel and reopen the editor to load the latest layout.'
    : error?.detail ? `${error.detail} Your edits are still open.`
    : error?.status === 400
      ? 'Check your titles and measurements. An assigned source may have changed since you opened the editor.'
      : 'The dashboard could not be saved. Your edits are still open.'

  return <form className="monitor-editor" onSubmit={submit} aria-label="Dashboard editor">
    <div className="monitor-editor-heading">
      <div><h2>Configure dashboard</h2><p className="muted">{property.name}</p></div>
      <div className="monitor-actions">
        <Button variant="ghost" type="button" onClick={done} disabled={saving.isPending}>Cancel</Button>
        <Button type="submit" disabled={!catalog.isSuccess || saving.isPending}>{saving.isPending ? 'Saving…' : 'Save dashboard'}</Button>
      </div>
    </div>
    {catalog.isPending && <p role="status">Loading assigned measurements…</p>}
    {catalog.isError && <p role="alert">Assigned measurements could not be loaded. <Button type="button" variant="outline"
      onClick={() => void catalog.refetch()}>Retry</Button></p>}
    {(validation || saving.isError) && <p className="form-error" role="alert">{validation || saveError}</p>}
    <fieldset disabled={!catalog.isSuccess || saving.isPending} className="monitor-editor-fields">
      {groups.map((group, index) => <GroupEditor key={group.id} group={group} index={index}
        sources={sources} previewSources={previewSources} property={property} now={now} count={groups.length}
        update={updated => setGroups(change(groups, index, updated))} shift={by => setGroups(move(groups, index, by))}
        discard={() => setGroups(remove(groups, index))} />)}
      <Button type="button" variant="outline" disabled={groups.length >= 24}
        onClick={() => setGroups([...groups, newGroup()])}><Plus size={16} />Add group</Button>
    </fieldset>
  </form>
}

function GroupEditor({ group, index, count, sources, previewSources, property, now, update, shift, discard }: {
  group: DashboardGroup; index: number; count: number; sources: DashboardSource[]; previewSources: Map<string, DashboardSource>
  property: Property; now: number; update: (group: DashboardGroup) => void; shift: (by: number) => void; discard: () => void
}) {
  return <fieldset className="monitor-edit-group">
    <legend>{group.title || 'New group'}</legend>
    <div className="monitor-edit-line">
      <label>Group title<Input required maxLength={96} value={group.title}
        onChange={event => update({ ...group, title: event.target.value })} /></label>
      <Reorder label={group.title || 'group'} index={index} count={count} shift={shift} discard={discard} />
    </div>
    <div className="monitor-edit-options">
      <label><input type="checkbox" checked={group.hidden} onChange={event => update({ ...group, hidden: event.target.checked })} /> Hide group</label>
    </div>
    <div className="monitor-edit-widgets">
      {group.widgets.map((widget, position) => <WidgetEditor key={widget.id} widget={widget} index={position} count={group.widgets.length}
        sources={sources} previewSources={previewSources} property={property} now={now}
        update={updated => update({ ...group, widgets: change(group.widgets, position, updated) })}
        shift={by => update({ ...group, widgets: move(group.widgets, position, by) })}
        discard={() => update({ ...group, widgets: remove(group.widgets, position) })} />)}
    </div>
    <Button variant="outline" type="button" disabled={group.widgets.length >= 24}
      onClick={() => update({ ...group, widgets: [...group.widgets, newWidget()] })}><Plus size={16} />Add widget</Button>
  </fieldset>
}

function WidgetEditor({ widget, index, count, sources, previewSources, property, now, update, shift, discard }: {
  widget: DashboardWidget; index: number; count: number; sources: DashboardSource[]; previewSources: Map<string, DashboardSource>
  property: Property; now: number; update: (widget: DashboardWidget) => void; shift: (by: number) => void; discard: () => void
}) {
  const list = useRef<HTMLDivElement>(null)
  const drag = useRef<{ id: string; pointer: number }>(null)
  const [dropTarget, setDropTarget] = useState<string>()
  function updateValues(values: DashboardValue[]) {
    update({ ...widget, values, navigationSource: values.some(value => value.sourceKey === widget.navigationSource)
      ? widget.navigationSource : null })
  }
  function pointerTarget(event: PointerEvent) {
    const item = document.elementFromPoint(event.clientX, event.clientY)?.closest<HTMLElement>('[data-dashboard-value]')
    return item && item.parentElement === list.current ? item.dataset.dashboardValue : undefined
  }
  function clearDrag() { drag.current = null; setDropTarget(undefined) }
  return <fieldset className="monitor-edit-widget">
    <legend>{widget.title || 'New widget'}</legend>
    <div className="monitor-edit-line">
      <label>Widget title<Input required maxLength={96} value={widget.title}
        onChange={event => update({ ...widget, title: event.target.value })} /></label>
      <label>Display<select value={widget.mode} onChange={event => update({ ...widget, mode: event.target.value === 'detailed' ? 'detailed' : 'compact' })}>
        <option value="compact">Compact</option><option value="detailed">Detailed</option>
      </select></label>
      <label>Widget width<select value={widget.width ?? 1} onChange={event => update({ ...widget, width: Number(event.target.value) })}>
        {columns.map(count => <option key={count} value={count}>{count === 4 ? '4 columns / full width' : count}</option>)}
      </select></label>
      <label>Measurement columns<select value={widget.measurementColumns ?? 2}
        onChange={event => update({ ...widget, measurementColumns: Number(event.target.value) })}>
        {columns.map(count => <option key={count} value={count}>{count}</option>)}
      </select></label>
      <Reorder label={widget.title || 'widget'} index={index} count={count} shift={shift} discard={discard} />
    </div>
    <div className="monitor-edit-values" ref={list} onPointerMove={event => {
      if (drag.current?.pointer === event.pointerId) setDropTarget(pointerTarget(event))
    }} onPointerUp={event => {
      if (drag.current?.pointer !== event.pointerId) return
      const from = widget.values.findIndex(value => value.id === drag.current?.id)
      const target = pointerTarget(event)
      const to = widget.values.findIndex(value => value.id === target)
      if (from >= 0 && to >= 0 && from !== to) updateValues(move(widget.values, from, to - from))
      clearDrag()
    }} onPointerCancel={clearDrag} onLostPointerCapture={clearDrag}>
      {widget.values.map((value, position) => <div key={value.id} data-dashboard-value={value.id}
        className={dropTarget === value.id ? 'monitor-edit-value-target' : undefined}>
        <ValueEditor value={value} position={position} count={widget.values.length}
          sources={sources} update={updated => updateValues(change(widget.values, position, updated))}
          shift={by => updateValues(move(widget.values, position, by))}
          discard={() => updateValues(remove(widget.values, position))}
          startDrag={event => {
            if (widget.values.length < 2 || event.button !== 0 || !event.isPrimary) return
            drag.current = { id: value.id, pointer: event.pointerId }
            event.currentTarget.setPointerCapture(event.pointerId)
          }} />
      </div>)}
    </div>
    <div className="monitor-edit-line">
      <Button variant="outline" type="button" disabled={widget.values.length >= 16}
        onClick={() => updateValues([...widget.values, newValue()])}><Plus size={16} />Add measurement</Button>
    </div>
    <details className="monitor-preview">
      <summary>Preview widget</summary>
      <MonitoringWidget widget={widget} sources={previewSources} property={property} now={now} preview />
    </details>
  </fieldset>
}

function ValueEditor({ value, position, count, sources, update, shift, discard, startDrag }: {
  value: DashboardValue; position: number; count: number; sources: DashboardSource[]
  update: (value: DashboardValue) => void; shift: (by: number) => void; discard: () => void
  startDrag: (event: PointerEvent<HTMLButtonElement>) => void
}) {
  const selected = sources.find(source => source.key === value.sourceKey)
  const [provider, setProvider] = useState(selected?.provider ?? '')
  const [device, setDevice] = useState(selected?.deviceId ?? '')
  const providers = [...new Set(sources.map(source => source.provider))]
  const devices = new Map(sources.filter(source => source.provider === provider).map(source => [source.deviceId, source.deviceName]))
  const measurements = sources.filter(source => source.provider === provider && source.deviceId === device)
  return <fieldset className="monitor-edit-value">
    <legend>Measurement {position + 1}</legend>
    <div className="monitor-edit-line">
      <label>Provider<select value={provider} onChange={event => {
        const selectedProvider = providers.find(item => item === event.target.value) ?? ''
        setProvider(selectedProvider)
        setDevice('')
        update({ ...value, sourceKey: '' })
      }}>
        <option value="">Choose a provider</option>
        {providers.map(item => <option key={item} value={item}>{providerNames[item]}</option>)}
      </select></label>
      <label>Device<select value={device} disabled={!provider} onChange={event => {
        setDevice(event.target.value)
        update({ ...value, sourceKey: '' })
      }}>
        <option value="">Choose a device</option>
        {[...devices].map(([id, name]) => <option key={id} value={id}>{name}</option>)}
      </select></label>
      <label>Measurement<select value={value.sourceKey} disabled={!device}
        onChange={event => update({ ...value, sourceKey: event.target.value })}>
        <option value="">Choose a measurement</option>
        {!selected && value.sourceKey && <option value={value.sourceKey}>Unavailable source</option>}
        {measurements.map(source => <option key={source.key} value={source.key}>
          {metricLabels[source.metric]}{source.channel ? ` · ${source.channel}` : ''}{source.unit ? ` (${source.unit})` : ''}
        </option>)}
      </select></label>
      <label>Custom label<Input maxLength={96} value={value.label ?? ''}
        onChange={event => update({ ...value, label: event.target.value || null })} /></label>
      <label>Value color<select value={value.colorMode === 'fixed' ? value.customColor ?? '' : ''} onChange={event => {
        const customColor = event.target.value || null
        update({ ...value, colorMode: customColor ? 'fixed' : 'automatic', customColor })
      }}>
        <option value="">Automatic (freshness)</option>
        {Object.entries(colorPresets).map(([color, name]) => <option key={color} value={color}>{name}</option>)}
        {value.colorMode === 'fixed' && value.customColor && !Object.hasOwn(colorPresets, value.customColor) &&
          <option value={value.customColor}>{value.customColor}</option>}
      </select></label>
      <Button variant="ghost" type="button" className="monitor-value-grip" title="Drag to reorder"
        aria-label={`Drag measurement ${position + 1}`} disabled={count < 2} onPointerDown={startDrag}><GripVertical size={16} /></Button>
      <Reorder label={`measurement ${position + 1}`} index={position} count={count} shift={shift} discard={discard} />
    </div>
  </fieldset>
}

function Reorder({ label, index, count, shift, discard }: {
  label: string; index: number; count: number; shift: (by: number) => void; discard: () => void
}) {
  return <div className="monitor-actions">
    <Button variant="ghost" type="button" title="Move up" aria-label={`Move ${label} up`} disabled={index === 0}
      onClick={() => shift(-1)}><ArrowUp size={16} /></Button>
    <Button variant="ghost" type="button" title="Move down" aria-label={`Move ${label} down`} disabled={index === count - 1}
      onClick={() => shift(1)}><ArrowDown size={16} /></Button>
    <Button variant="ghost" type="button" title="Remove" aria-label={`Remove ${label}`} onClick={discard}><Trash2 size={16} /></Button>
  </div>
}
