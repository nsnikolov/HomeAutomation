import { useRef, useState, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { GripVertical } from 'lucide-react'
import { api } from '../shared/api'
import type { DevicePageKey, DevicePreferences } from '../shared/types'

type Change = { group: string; ids: string[] } | { id: string; hidden: boolean }
type Card = { id: string | number; name: string }
const empty: DevicePreferences = { order: {}, hidden: [] }

export function useDevicePreferences(property: number, page: DevicePageKey) {
  const cache = useQueryClient()
  const account = cache.getQueryData<{ email: string }>(['session'])?.email
  const key = ['property', property, 'device-preferences', page, account]
  const query = useQuery({
    queryKey: key, queryFn: ({ signal }) => api.devicePreferences(property, page, signal), retry: false,
  })
  const mutation = useMutation({
    mutationFn: (change: Change) => 'group' in change ? api.orderDevices(property, page, change.group, change.ids) :
      api.setTuyaVisibility(property, change.id, change.hidden),
    onMutate: async change => {
      await cache.cancelQueries({ queryKey: key })
      const previous = cache.getQueryData<DevicePreferences>(key)
      const current = previous ?? empty
      cache.setQueryData<DevicePreferences>(key, 'group' in change
        ? { ...current, order: { ...current.order, [change.group]: change.ids } }
        : { ...current, hidden: change.hidden ? [...new Set([...current.hidden, change.id])] :
          current.hidden.filter(id => id !== change.id) })
      return previous
    },
    onError: (_error, _change, previous) => cache.setQueryData(key, previous),
    onSettled: () => cache.invalidateQueries({ queryKey: key }),
  })
  return {
    data: query.data ?? empty, ready: query.isSuccess, loading: query.isPending, busy: mutation.isPending,
    error: mutation.isError ? 'Could not save device preferences.' : query.isError ? 'Device preferences are unavailable.' : '',
    update: mutation.mutate,
  }
}

export function orderDevices<T extends Card>(items: readonly T[], saved: readonly string[] = []) {
  const indices = new Map(saved.map((id, index) => [id, index]))
  return items.toSorted((a, b) => (indices.get(String(a.id)) ?? saved.length) - (indices.get(String(b.id)) ?? saved.length))
}

export function moveVisible(saved: string[], all: readonly Card[], visible: readonly Card[], from: string, to: string) {
  const shown = visible.map(item => String(item.id))
  const start = shown.indexOf(from)
  const end = shown.indexOf(to)
  if (start < 0 || end < 0 || start === end) return undefined
  const ordered = [...new Set([...saved, ...all.map(item => String(item.id))])]
  const visibleIds = new Set(shown)
  shown.splice(start, 1)
  shown.splice(end, 0, from)
  let next = 0
  return ordered.map(id => visibleIds.has(id) ? shown[next++] : id)
}

export function DeviceCardOrder<T extends Card>({ items, group, preferences, rows = false, visible, children }: {
  items: readonly T[]; group: string; preferences?: ReturnType<typeof useDevicePreferences>;
  rows?: boolean; visible?: (item: T) => boolean; children: (item: T) => ReactNode
}) {
  const saved = preferences?.data.order[group] ?? []
  const sorted = orderDevices(items, saved).filter(visible ?? (() => true))
  const [dragTarget, setDragTarget] = useState<string>()
  const [announcement, setAnnouncement] = useState('')
  const drag = useRef<{ id: string; pointer: number }>(null)
  const list = useRef<HTMLElement>(null)
  const movable = preferences?.ready && !preferences.busy && sorted.length > 1
  const Container = rows ? 'ul' : 'div'
  const Item = rows ? 'li' : 'div'

  function target(x: number, y: number) {
    const element = document.elementFromPoint(x, y)?.closest<HTMLElement>('[data-ordered-id]')
    return element && element.parentElement === list.current ? element.dataset.orderedId : undefined
  }
  function move(from: string, to: string) {
    const ids = moveVisible(saved, items, sorted, from, to)
    if (ids && movable) {
      preferences.update({ group, ids })
      setAnnouncement(`Moved ${sorted.find(item => String(item.id) === from)?.name}.`)
    }
  }
  return <>
    <Container className={rows ? 'rows' : 'device-grid'}
      ref={(element: HTMLElement | null) => { list.current = element }}>
      {sorted.map((item, index) => {
        const id = String(item.id)
        return <Item key={id} data-ordered-id={id} className={`ordered-card ${dragTarget === id ? 'order-target' : ''}`}>
          <div className={rows ? 'ordered-row-content' : 'ordered-card-content'}>{children(item)}</div>
          {preferences?.ready && sorted.length > 1 && <button type="button" className="order-handle"
            aria-label={`Reorder ${item.name}`} aria-keyshortcuts="ArrowUp ArrowDown" aria-disabled={!movable}
            title="Drag to reorder, or use the Up and Down arrow keys"
            onKeyDown={event => {
              const indexOffset = event.key === 'ArrowUp' ? -1 : event.key === 'ArrowDown' ? 1 : 0
              if (!indexOffset) return
              event.preventDefault()
              const neighbor = sorted[index + indexOffset]
              if (neighbor) move(id, String(neighbor.id))
            }}
            onPointerDown={event => {
              if (!movable || event.button !== 0 || !event.isPrimary) return
              drag.current = { id, pointer: event.pointerId }
              event.currentTarget.setPointerCapture(event.pointerId)
            }}
            onPointerMove={event => {
              if (drag.current?.pointer === event.pointerId) setDragTarget(target(event.clientX, event.clientY))
            }}
            onPointerUp={event => {
              if (drag.current?.pointer !== event.pointerId) return
              const to = target(event.clientX, event.clientY)
              if (to) move(drag.current.id, to)
              drag.current = null
              setDragTarget(undefined)
            }}
            onLostPointerCapture={() => { drag.current = null; setDragTarget(undefined) }}>
            <GripVertical size={17} aria-hidden />
          </button>}
        </Item>
      })}
    </Container>
    {announcement && <span className="sr-only" role="status">{announcement}</span>}
  </>
}
