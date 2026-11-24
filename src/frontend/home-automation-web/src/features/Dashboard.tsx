import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Settings2 } from 'lucide-react'
import { api, ApiError } from '../shared/api'
import type { DashboardSource, DashboardWidget, Property } from '../shared/types'
import { Button, Card } from '../shared/ui'
import { DashboardEditor, dashboardKey } from './DashboardEditor'
import { DashboardGroups, useDashboardTime } from './DashboardWidgets'

const emptySources: DashboardSource[] = []

export function Dashboard({ property, live }: { property: Property; live: boolean }) {
  const [editing, setEditing] = useState(false)
  const [viewMode, setViewMode] = useState<DashboardWidget['mode']>()
  const query = useQuery({
    queryKey: dashboardKey(property.id),
    queryFn: ({ signal }) => api.dashboard(property.id, signal),
    refetchInterval: live ? 30000 : 10000,
  })
  const snapshot = query.data
  const listedSources = snapshot?.sources ?? emptySources
  const sources = useMemo(() => new Map(listedSources.map(source => [source.key, source])), [listedSources])
  const now = useDashboardTime(listedSources)
  const hasGroups = snapshot?.groups.some(group => !group.hidden)
  return <>
    <div className="page-heading page-heading-inline">
      <h1>Dashboard</h1>
      {hasGroups && !editing && <div className="monitor-header-actions">
        <div className="monitor-view-toggle" role="group" aria-label="Dashboard display mode"
          title="Select a mode for all widgets. Select it again to restore the saved modes.">
          {(['compact', 'detailed'] as const).map(mode => <button key={mode} type="button"
            aria-pressed={viewMode === mode} onClick={() => setViewMode(current => current === mode ? undefined : mode)}>
            {mode === 'compact' ? 'Compact' : 'Detailed'}
          </button>)}
        </div>
        {snapshot?.canEdit && <Button variant="outline" onClick={() => setEditing(true)}>
          <Settings2 size={16} />Configure dashboard
        </Button>}
      </div>}
    </div>
    {query.isPending && <p role="status">Loading dashboard…</p>}
    {query.isError && <p className="notice" role="alert">
      {query.error instanceof ApiError && query.error.status === 503
        ? 'The dashboard database migration is required.' : 'The dashboard could not be refreshed.'}
      {' '}<Button type="button" variant="outline" onClick={() => void query.refetch()}>Retry</Button>
    </p>}
    {snapshot && (editing && snapshot.canEdit ? <DashboardEditor property={property} snapshot={snapshot} done={() => setEditing(false)} />
      : hasGroups ? <DashboardGroups groups={snapshot.groups} sources={sources} property={property} now={now} viewMode={viewMode} />
      : <Card className="monitor-empty">
        <h2>No monitoring widgets yet</h2>
        <p>{snapshot.canEdit ? 'Create a group and choose measurements from devices assigned to this property.'
          : 'A property operator can configure this dashboard.'}</p>
        {snapshot.canEdit && <Button onClick={() => setEditing(true)}><Settings2 size={16} />Configure dashboard</Button>}
      </Card>)}
  </>
}
