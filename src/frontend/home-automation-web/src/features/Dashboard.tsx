import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Settings2 } from 'lucide-react'
import { api, ApiError } from '../shared/api'
import type { DashboardSource, DashboardWidget, Property } from '../shared/types'
import { Button, Card } from '../shared/ui'
import { DashboardEditor, dashboardKey } from './DashboardEditor'
import { DashboardGroups, DashboardViewToggle, useDashboardTime } from './DashboardWidgets'
import { useDashboardWidgetViews } from './dashboardWidgetViews'

const emptySources: DashboardSource[] = []

type DashboardProps = { property: Property; live: boolean; email: string }

export function Dashboard(props: DashboardProps) {
  return <PropertyDashboard key={JSON.stringify([props.email, props.property.id])} {...props} />
}

function PropertyDashboard({ property, live, email }: DashboardProps) {
  const [editing, setEditing] = useState(false)
  const [modeSelected, setModeSelected] = useState<DashboardWidget['mode']>()
  const query = useQuery({
    queryKey: dashboardKey(property.id),
    queryFn: ({ signal }) => api.dashboard(property.id, signal),
    refetchInterval: live ? 30000 : 10000,
  })
  const snapshot = query.data
  const widgets = useMemo(() => snapshot?.groups.flatMap(group => group.widgets), [snapshot?.groups])
  const { ready, views, update } = useDashboardWidgetViews(email, property.id,
    query.isSuccess && query.isFetchedAfterMount && !query.isFetching ? widgets : undefined)
  const listedSources = snapshot?.sources ?? emptySources
  const sources = useMemo(() => new Map(listedSources.map(source => [source.key, source])), [listedSources])
  const now = useDashboardTime(listedSources)
  const hasGroups = snapshot?.groups.some(group => !group.hidden)
  return <>
    <div className="page-heading page-heading-inline">
      <h1>Dashboard</h1>
      {hasGroups && ready && !editing && <div className="monitor-header-actions">
        <DashboardViewToggle label="Dashboard display mode" selected={modeSelected}
          title="Compact keeps measurement visibility. Detailed expands all measurements."
          onSelect={mode => {
            setModeSelected(mode)
            update(widgets?.map(widget => widget.id) ?? [], mode === 'detailed' ? { mode, expanded: true } : { mode })
          }} />
        {snapshot?.canEdit && <Button variant="outline" onClick={() => setEditing(true)}>
          <Settings2 size={16} />Configure dashboard
        </Button>}
      </div>}
    </div>
    {(query.isPending || (hasGroups && !ready)) && <p role="status">Loading dashboard…</p>}
    {query.isError && <p className="notice" role="alert">
      {(query.error instanceof ApiError && query.error.detail) || 'The dashboard could not be refreshed.'}
      {' '}<Button type="button" variant="outline" onClick={() => void query.refetch()}>Retry</Button>
    </p>}
    {snapshot && (editing && snapshot.canEdit ? <DashboardEditor property={property} snapshot={snapshot} done={() => setEditing(false)} />
      : hasGroups ? ready && <DashboardGroups groups={snapshot.groups} sources={sources} property={property} now={now}
        views={views} onViewChange={(id, change) => update([id], change)} />
      : <Card className="monitor-empty">
        <h2>No monitoring widgets yet</h2>
        <p>{snapshot.canEdit ? 'Create a group and choose measurements from devices assigned to this property.'
          : 'A property operator can configure this dashboard.'}</p>
        {snapshot.canEdit && <Button onClick={() => setEditing(true)}><Settings2 size={16} />Configure dashboard</Button>}
      </Card>)}
  </>
}
