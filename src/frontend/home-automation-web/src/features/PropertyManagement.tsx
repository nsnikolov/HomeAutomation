import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate } from 'react-router'
import { api, LogicalApiError } from '../shared/api'
import { ActionMenu } from '../shared/ActionMenu'
import { ConfirmDialog } from '../shared/ConfirmDialog'
import type { ManagedRoom, Property, PropertyDeletionImpact, PropertyOverview } from '../shared/types'
import { Badge, Button, Card, Input } from '../shared/ui'

export const defaultTimeZone = 'Europe/Sofia'
const message = (error: unknown, fallback: string) => error instanceof LogicalApiError ? error.message : fallback
const plural = (count: number, noun: string) => `${count} ${noun}${count === 1 ? '' : 's'}`

/** Refreshes every view that shows properties, rooms or devices after an administrative change. */
export async function refreshManagement(client: ReturnType<typeof useQueryClient>, property?: number) {
  await Promise.all([
    client.invalidateQueries({ queryKey: ['properties'] }),
    client.invalidateQueries({ queryKey: ['property-management'] }),
    property === undefined ? Promise.resolve() : client.invalidateQueries({ queryKey: ['property', property] }),
  ])
}

/** Properties the user can access, with create, edit and delete for the roles the server allows. */
export function PropertyManagement({ current }: { current: Property }) {
  const client = useQueryClient()
  const navigate = useNavigate()
  const list = useQuery({ queryKey: ['property-management'], queryFn: ({ signal }) => api.propertyManagement(signal), retry: false })
  const [creating, setCreating] = useState(false)
  const [editing, setEditing] = useState<number | null>(null)
  const [deleting, setDeleting] = useState<PropertyOverview | null>(null)
  const [notice, setNotice] = useState('')

  if (list.isPending) return <p role="status">Loading properties…</p>
  if (list.isError) return <p role="alert">Properties could not be loaded. <Button variant="outline" onClick={() => void list.refetch()}>Retry</Button></p>
  const { canCreate, properties } = list.data

  return <section aria-labelledby="properties-heading" className="room">
    <div className="card-heading">
      <h2 id="properties-heading">Properties</h2>
      {canCreate && !creating && <Button variant="outline" type="button" onClick={() => { setCreating(true); setNotice('') }}>Add property</Button>}
    </div>
    {notice && <p role="status">{notice}</p>}
    {creating && <CreatePropertyForm onCancel={() => setCreating(false)} onCreated={async (id, name) => {
      setCreating(false)
      await refreshManagement(client)
      setNotice(`${name} was created with no rooms or devices.`)
      void navigate(`/properties/${id}/settings`)
    }} />}
    <div className="management-list">
      {properties.map(item => <Card className="detail-card" key={item.id}>
        {editing === item.id
          ? <EditPropertyForm property={item} onCancel={() => setEditing(null)} onSaved={async () => {
            setEditing(null)
            await refreshManagement(client, item.id)
            setNotice(`${item.name} was saved.`)
          }} />
          : <div className="management-row">
            <div>
              <h3>{item.id === current.id ? item.name : <Link to={`/properties/${item.id}/settings`}>{item.name}</Link>}
                {' '}{item.id === current.id && <Badge>Active property</Badge>}</h3>
              {item.description && <p className="muted">{item.description}</p>}
              <dl>
                <div><dt>Rooms</dt><dd>{item.rooms}</dd></div>
                <div><dt>Devices</dt><dd>{item.devices}</dd></div>
                <div><dt>Time zone</dt><dd>{item.timeZone}</dd></div>
                {item.growattProfile !== null && <div><dt>Growatt profile</dt><dd>{item.growattProfile ? 'Stored' : 'Not stored'}</dd></div>}
                <div><dt>Your role</dt><dd>{item.role}</dd></div>
              </dl>
            </div>
            {(item.canManage || item.canDelete) && <ActionMenu label={`Actions for ${item.name}`} items={[
              ...(item.canManage ? [{ label: 'Edit property', onSelect: () => setEditing(item.id) }] : []),
              ...(item.canDelete ? [{ label: 'Delete property…', destructive: true, onSelect: () => { setNotice(''); setDeleting(item) } }] : []),
            ]} />}
          </div>}
      </Card>)}
    </div>
    {deleting && <DeleteProperty property={deleting} onCancel={() => setDeleting(null)} onDeleted={async () => {
      const deleted = deleting
      setDeleting(null)
      setNotice(`${deleted.name} was deleted.`)
      await refreshManagement(client)
      if (deleted.id === current.id) void navigate('/', { replace: true })
    }} />}
  </section>
}

export function CreatePropertyForm({ onCreated, onCancel }: { onCreated: (id: number, name: string) => Promise<void> | void; onCancel?: () => void }) {
  // One request id per form keeps a repeated submission from creating a second property.
  const [requestId] = useState(() => crypto.randomUUID())
  const create = useMutation({
    mutationFn: (values: { name: string; description: string | null; timeZone: string }) => api.createProperty({ ...values, requestId }),
  })
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (create.isPending) return
    const data = new FormData(event.currentTarget)
    const name = String(data.get('name') ?? '').trim()
    if (!name) return
    const result = await create.mutateAsync({ name, description: String(data.get('description') ?? '').trim() || null,
      timeZone: String(data.get('timeZone') ?? '').trim() || defaultTimeZone }).catch(() => null)
    if (result) await onCreated(result.id, name)
  }
  return <form className="detail-card settings-form" aria-label="Add property" onSubmit={event => void submit(event)}>
    <label>Name<Input name="name" required maxLength={128} /></label>
    <label>Description (optional)<Input name="description" maxLength={256} /></label>
    <label>Time zone<Input name="timeZone" defaultValue={defaultTimeZone} maxLength={64} /></label>
    <p className="muted">A new property starts with no rooms, devices or provider settings.</p>
    {create.isError && <p role="alert" className="form-error">{message(create.error, 'The property could not be created.')}</p>}
    <div className="monitor-actions">
      <Button type="submit" disabled={create.isPending}>{create.isPending ? 'Creating…' : 'Create property'}</Button>
      {onCancel && <Button variant="ghost" type="button" onClick={onCancel}>Cancel</Button>}
    </div>
  </form>
}

function EditPropertyForm({ property, onSaved, onCancel }: { property: PropertyOverview; onSaved: () => Promise<void>; onCancel: () => void }) {
  const save = useMutation({
    mutationFn: (values: { name: string; description: string | null; timeZone: string }) => api.updateProperty(property.id, values),
    onSuccess: onSaved,
  })
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (save.isPending) return
    const data = new FormData(event.currentTarget)
    save.mutate({ name: String(data.get('name') ?? '').trim(), description: String(data.get('description') ?? '').trim() || null,
      timeZone: String(data.get('timeZone') ?? '').trim() })
  }
  return <form className="settings-form" aria-label={`Edit ${property.name}`} onSubmit={submit}>
    <label>Name<Input name="name" required maxLength={128} defaultValue={property.name} /></label>
    <label>Description (optional)<Input name="description" maxLength={256} defaultValue={property.description ?? ''} /></label>
    <label>Time zone<Input name="timeZone" required maxLength={64} defaultValue={property.timeZone} /></label>
    <p className="muted">Editing the name, description or time zone does not change devices, history or provider settings.</p>
    {save.isError && <p role="alert" className="form-error">{message(save.error, 'The property could not be saved.')}</p>}
    <div className="monitor-actions">
      <Button type="submit" disabled={save.isPending}>{save.isPending ? 'Saving…' : 'Save property'}</Button>
      <Button variant="ghost" type="button" onClick={onCancel}>Cancel</Button>
    </div>
  </form>
}

/** Reviews the current server-side impact before deletion; only current devices block it. */
function DeleteProperty({ property, onDeleted, onCancel }: { property: PropertyOverview; onDeleted: () => Promise<void>; onCancel: () => void }) {
  const impact = useQuery({
    queryKey: ['property', property.id, 'deletion-impact'], queryFn: ({ signal }) => api.deletionImpact(property.id, signal),
    staleTime: 0, gcTime: 0, retry: false,
  })
  const data: PropertyDeletionImpact | undefined = impact.data
  return <ConfirmDialog open title={`Delete ${property.name}?`} confirmLabel="Delete property" destructive typedConfirmation={property.name}
    typedLabel={<>Type the property name <strong>{property.name}</strong> to confirm</>}
    blocked={!data || data.devices > 0} onCancel={onCancel}
    onConfirm={async () => { await api.deleteProperty(property.id, property.name); await onDeleted() }}>
    {impact.isPending && <p role="status">Checking what this property contains…</p>}
    {impact.isError && <p role="alert">The deletion impact could not be loaded. Cancel and try again.</p>}
    {data && <>
      <ul aria-label="Deletion impact">
        <li>{plural(data.devices, 'device')}{data.devices > 0 && ' (blocks deletion)'}</li>
        <li>{plural(data.rooms, 'room')}</li>
        <li>{plural(data.installations, 'installation')}</li>
        <li>{plural(data.members, 'member')} will lose access</li>
        {data.historicalDevices > 0 && <li>{plural(data.historicalDevices, 'device')} that moved to other properties, with their earlier history</li>}
        {data.vaultProfiles > 0 && <li>{plural(data.vaultProfiles, 'encrypted provider profile')}</li>}
      </ul>
      {data.devices > 0
        ? <p role="alert">Move or remove this property's devices before deleting it.</p>
        : <p>The property disappears for every member. Its rooms, history and encrypted provider settings are kept in the database and are
          not deleted.</p>}
    </>}
  </ConfirmDialog>
}

/** Optional rooms of a property: list, create, rename, describe and delete without deleting devices. */
export function RoomManagement({ property }: { property: Property }) {
  const client = useQueryClient()
  const key = ['property', property.id, 'managed-rooms']
  const rooms = useQuery({ queryKey: key, queryFn: ({ signal }) => api.managedRooms(property.id, signal), retry: false })
  const canManage = useQuery({ queryKey: ['property-management'], queryFn: ({ signal }) => api.propertyManagement(signal), retry: false })
    .data?.properties.find(item => item.id === property.id)?.canManage === true
  const [editing, setEditing] = useState<number | 'new' | null>(null)
  const [deleting, setDeleting] = useState<ManagedRoom | null>(null)
  const [notice, setNotice] = useState('')
  const refresh = () => refreshManagement(client, property.id)

  return <section aria-labelledby="rooms-heading" className="room">
    <div className="card-heading">
      <h2 id="rooms-heading">Rooms in {property.name}</h2>
      {canManage && editing !== 'new' && <Button variant="outline" type="button" onClick={() => { setEditing('new'); setNotice('') }}>Add room</Button>}
    </div>
    {notice && <p role="status">{notice}</p>}
    {editing === 'new' && <RoomForm property={property.id} onCancel={() => setEditing(null)}
      onSaved={async name => { setEditing(null); await refresh(); setNotice(`${name} was added.`) }} />}
    {rooms.isPending && <p role="status">Loading rooms…</p>}
    {rooms.isError && <p role="alert">Rooms could not be loaded.</p>}
    {rooms.data?.length === 0 && editing !== 'new' && <Card className="placeholder"><p className="muted">No rooms yet. Add your first room.</p></Card>}
    <div className="management-list">
      {rooms.data?.map(room => <Card className="detail-card" key={room.id}>
        {editing === room.id
          ? <RoomForm property={property.id} room={room} onCancel={() => setEditing(null)}
            onSaved={async name => { setEditing(null); await refresh(); setNotice(`${name} was saved.`) }} />
          : <div className="management-row">
            <div><h3>{room.name}</h3>{room.description && <p className="muted">{room.description}</p>}
              <p className="muted">{plural(room.devices, 'device')}</p></div>
            {canManage && <ActionMenu label={`Actions for ${room.name}`} items={[
              { label: 'Edit room', onSelect: () => setEditing(room.id) },
              { label: 'Delete room…', destructive: true, onSelect: () => { setNotice(''); setDeleting(room) } },
            ]} />}
          </div>}
      </Card>)}
    </div>
    <ConfirmDialog open={deleting !== null} title={`Delete ${deleting?.name ?? 'room'}?`} confirmLabel="Delete room" destructive
      onCancel={() => setDeleting(null)}
      onConfirm={async () => {
        const room = deleting!
        await api.deleteRoom(property.id, room.id)
        setDeleting(null)
        await refresh()
        setNotice(`${room.name} was deleted. Its devices stay in ${property.name}.`)
      }}>
      {deleting && <>
        <p>{plural(deleting.devices, 'device')} {deleting.devices === 1 ? 'is' : 'are'} currently in this room.</p>
        <p>Devices are not deleted. They stay in {property.name} without a room, with their history, provider connections and settings.</p>
      </>}
    </ConfirmDialog>
  </section>
}

function RoomForm({ property, room, onSaved, onCancel }: {
  property: number; room?: ManagedRoom; onSaved: (name: string) => Promise<void>; onCancel: () => void
}) {
  const save = useMutation({
    mutationFn: (values: { name: string; description: string | null }) =>
      room ? api.updateRoom(property, room.id, values) : api.createRoom(property, values),
    onSuccess: (_, values) => onSaved(values.name),
  })
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (save.isPending) return
    const data = new FormData(event.currentTarget)
    const name = String(data.get('name') ?? '').trim()
    if (name) save.mutate({ name, description: String(data.get('description') ?? '').trim() || null })
  }
  return <form className="settings-form" aria-label={room ? `Edit ${room.name}` : 'Add room'} onSubmit={submit}>
    <label>Room name<Input name="name" required maxLength={128} defaultValue={room?.name ?? ''} /></label>
    <label>Description (optional)<Input name="description" maxLength={256} defaultValue={room?.description ?? ''} /></label>
    {save.isError && <p role="alert" className="form-error">{message(save.error, 'The room could not be saved.')}</p>}
    <div className="monitor-actions">
      <Button type="submit" disabled={save.isPending}>{save.isPending ? 'Saving…' : room ? 'Save room' : 'Add room'}</Button>
      <Button variant="ghost" type="button" onClick={onCancel}>Cancel</Button>
    </div>
  </form>
}

/** The create form shown to an installation administrator who has no property yet. */
export function FirstPropertyForm() {
  const client = useQueryClient()
  const navigate = useNavigate()
  return <CreatePropertyForm onCreated={async id => {
    await refreshManagement(client)
    void navigate(`/properties/${id}/settings`)
  }} />
}
