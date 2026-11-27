import { lazy, Suspense, useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowUpRight, ChartNoAxesCombined, ChevronDown, CircuitBoard, History as HistoryIcon, House, LayoutGrid, LogOut, Settings, Snowflake, Zap } from 'lucide-react'
import { Link, NavLink, Navigate, Route, Routes, useMatch, useNavigate, useParams, useSearchParams } from 'react-router'
import { api } from './shared/api'
import { useLiveUpdates } from './shared/live'
import type { Property } from './shared/types'
import { Badge, Button, Card, Input } from './shared/ui'
import { Dashboard } from './features/Dashboard'
import { GrowattPanel } from './features/GrowattPanel'
import { DaikinPanel } from './features/DaikinPanel'
import { EwelinkPanel } from './features/EwelinkPanel'
import { TuyaPanel } from './features/TuyaPanel'
import { Devices, DevicePage } from './features/Devices'
import { Energy } from './features/Energy'
// History is loaded on demand so its charts do not enlarge the initial bundle.
const HistoryPage = lazy(() => import('./features/history/HistoryPage').then(module => ({ default: module.HistoryPage })))
// Settings and property management are administrative pages, loaded on demand like History.
const ConfigurationSettings = lazy(() => import('./features/ConfigurationSettings').then(module => ({ default: module.ConfigurationSettings })))
const FirstPropertyForm = lazy(() => import('./features/PropertyManagement').then(module => ({ default: module.FirstPropertyForm })))

export function App() {
  const session = useQuery({ queryKey: ['session'], queryFn: ({ signal }) => api.session(signal) })
  if (session.isPending) return <p className="page-notice" role="status">Checking your session…</p>
  if (session.isError) return <Failure message="The monitoring service is unavailable." retry={() => void session.refetch()} />
  if (!session.data) return <Login />
  return <PropertyRoutes email={session.data.email} />
}

function Login() {
  const client = useQueryClient()
  const navigate = useNavigate()
  const daikinRoute = useMatch('/properties/:propertyId/daikin')
  const ewelinkRoute = useMatch('/properties/:propertyId/ewelink')
  const [parameters] = useSearchParams()
  const finishingConnection = (daikinRoute && parameters.get('onecta') === 'finish') ||
    (ewelinkRoute && ['success', 'failed'].includes(parameters.get('connect') ?? ''))
  const login = useMutation({
    mutationFn: (values: { email: string; password: string }) => api.login(values.email, values.password),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ['session'] })
      if (!finishingConnection) void navigate('/')
    },
  })
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const data = new FormData(event.currentTarget)
    login.mutate({ email: String(data.get('email')), password: String(data.get('password')) })
  }

  return <main className="login-layout">
    <div className="login-frame">
      <Brand />
      <Card className="login-card">
        <h1>Sign in</h1>
        <p className="muted">View your property’s devices and energy.</p>
        <form onSubmit={submit}>
          <label htmlFor="email">Email</label>
          <Input id="email" name="email" type="email" autoComplete="username" required />
          <label htmlFor="password">Password</label>
          <Input id="password" name="password" type="password" autoComplete="current-password" required />
          {login.isError && <p className="form-error" role="alert">Sign-in failed. Check your credentials or try again shortly.</p>}
          <Button disabled={login.isPending}>{login.isPending ? 'Signing in…' : 'Sign in'}<ArrowUpRight size={16} /></Button>
        </form>
      </Card>
    </div>
  </main>
}

function PropertyRoutes({ email }: { email: string }) {
  const properties = useQuery({ queryKey: ['properties'], queryFn: ({ signal }) => api.properties(signal) })
  if (properties.isPending) return <p className="page-notice" role="status">Loading properties…</p>
  if (properties.isError) return <Failure message="Properties could not be loaded." retry={() => void properties.refetch()} />

  return <Routes>
    <Route path="/properties/:propertyId/*" element={<SelectedProperty properties={properties.data} email={email} />} />
    <Route path="*" element={properties.data.length
      ? <Navigate to={`/properties/${properties.data[0].id}`} replace />
      : <NoProperties />} />
  </Routes>
}

/** Shown when the account has no property; an installation administrator can create the first one here. */
function NoProperties() {
  const management = useQuery({ queryKey: ['property-management'], queryFn: ({ signal }) => api.propertyManagement(signal), retry: false })
  return <div className="page-notice">
    <p>No properties are assigned to your account.</p>
    {management.data?.canCreate && <Suspense fallback={<p role="status">Loading…</p>}><FirstPropertyForm /></Suspense>}
    <SignOut />
  </div>
}

function SelectedProperty({ properties, email }: { properties: Property[]; email: string }) {
  const { propertyId } = useParams()
  const property = properties.find(item => String(item.id) === propertyId)
  if (!property) return <div className="page-notice"><p>This property is unavailable.</p><Link to="/">View your properties</Link></div>
  return <PropertyView key={property.id} property={property} properties={properties} email={email} />
}

const links = [
  { path: '', label: 'Dashboard', icon: LayoutGrid },
  { path: '/growatt', label: 'Live Growatt', icon: Zap },
  { path: '/daikin', label: 'Daikin ONECTA', icon: Snowflake },
  { path: '/ewelink', label: 'eWeLink Cloud', icon: CircuitBoard },
  { path: '/tuya', label: 'Smart Life / Tuya', icon: CircuitBoard },
  { path: '/devices', label: 'Devices', icon: CircuitBoard },
  { path: '/energy', label: 'Energy', icon: ChartNoAxesCombined },
  { path: '/history', label: 'History', icon: HistoryIcon },
  { path: '/automations', label: 'Automations', icon: Zap },
  { path: '/settings', label: 'Settings', icon: Settings },
]

function PropertyView({ property, properties, email }: { property: Property; properties: Property[]; email: string }) {
  const navigate = useNavigate()
  const growattView = useMatch('/properties/:propertyId/growatt') !== null
  const daikinView = useMatch('/properties/:propertyId/daikin') !== null
  const ewelinkView = useMatch('/properties/:propertyId/ewelink') !== null
  const tuyaView = useMatch('/properties/:propertyId/tuya') !== null
  const devicesView = useMatch('/properties/:propertyId/devices') !== null
  const energyView = useMatch('/properties/:propertyId/energy') !== null
  const stateView = devicesView || energyView
  const live = useLiveUpdates(property.id)
  const snapshot = useQuery({
    queryKey: ['property', property.id, 'state'],
    queryFn: ({ signal }) => api.state(property.id, signal),
    enabled: stateView,
    refetchInterval: live ? 30000 : 10000,
  })
  const health = useQuery({
    queryKey: ['health'], queryFn: ({ signal }) => api.health(signal), enabled: energyView, refetchInterval: 15000,
  })
  const [offlineNotice, setOfflineNotice] = useState(false)
  const root = `/properties/${property.id}`

  return <div className="shell">
    <aside className="sidebar">
      <Link to={root} className="brand-link" aria-label="Home Automation dashboard"><Brand /></Link>
      <nav aria-label="Main navigation">
        {links.map(({ path, label, icon: Icon }) =>
          <NavLink key={label} to={root + path} end={!path}><Icon size={18} /><span>{label}</span></NavLink>)}
      </nav>
      <div className="sidebar-foot"><div className="avatar">{email.slice(0, 1).toUpperCase()}</div><div className="account">{email}</div><SignOut iconOnly /></div>
    </aside>
    <div className="workspace">
      <header className="topbar">
        <div className="property-picker">
          <House size={17} aria-hidden />
          <select aria-label="Property" value={property.id} onChange={event => void navigate(`/properties/${event.target.value}`)}>
            {properties.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
          </select>
          <ChevronDown size={14} aria-hidden />
        </div>
        <div className="topbar-status">
          <span className="sample-label">{growattView ? 'Growatt Cloud' : daikinView ? 'Daikin ONECTA'
            : ewelinkView ? 'eWeLink Cloud' : tuyaView ? 'Tuya Cloud' : 'Property monitoring'}</span>
          <button className="live-toggle" type="button" aria-expanded={offlineNotice} onClick={() => setOfflineNotice(!offlineNotice)}>
            <Badge className={live ? 'tone-good' : 'tone-neutral'}><i />{live ? 'Live updates' : 'Auto refresh'}</Badge>
          </button>
        </div>
      </header>
      {offlineNotice && <div className="notice live-message" role="status">
        {live ? 'The dashboard receives updates over SignalR.' : 'The dashboard refreshes regularly while the live connection is unavailable.'}
      </div>}
      <main className="content">
        {stateView && snapshot.isError && <div className="notice" role="alert">The property feed could not be refreshed. <Button variant="outline" onClick={() => void snapshot.refetch()}>Retry</Button></div>}
        <Routes>
          <Route index element={<Dashboard property={property} live={live} email={email} />} />
          <Route path="growatt" element={<GrowattPanel property={property} />} />
          <Route path="daikin" element={<DaikinPanel property={property} />} />
          <Route path="ewelink" element={<EwelinkPanel property={property} />} />
          <Route path="tuya" element={<TuyaPanel property={property} />} />
          <Route path="devices" element={<Devices property={property} devices={snapshot.data?.devices} />} />
          <Route path="devices/:deviceId" element={<DevicePage property={property} />} />
          <Route path="energy" element={<Energy property={property} state={snapshot.data} pollSeconds={health.data?.pollSeconds ?? 10} />} />
          <Route path="history" element={<Suspense fallback={<p role="status">Loading history…</p>}><HistoryPage property={property} /></Suspense>} />
          <Route path="automations" element={<Placeholder title="Automations" description="Automation rules will appear here when rule execution is available." />} />
          <Route path="settings" element={<Suspense fallback={<p role="status">Loading settings…</p>}><ConfigurationSettings property={property} /></Suspense>} />
          <Route path="*" element={<Navigate to={root} replace />} />
        </Routes>
      </main>
    </div>
  </div>
}

function Placeholder({ title, description }: { title: string; description: string }) {
  return <><h1>{title}</h1><Card className="placeholder"><p>{description}</p></Card></>
}

function Failure({ message, retry }: { message: string; retry: () => void }) {
  return <div className="page-notice" role="alert"><p>{message}</p><Button variant="outline" onClick={retry}>Retry</Button></div>
}

function Brand() {
  return <div className="brand"><span className="brand-mark"><Zap size={19} fill="currentColor" /></span><span>Home Automation</span></div>
}

function SignOut({ iconOnly = false }: { iconOnly?: boolean }) {
  const client = useQueryClient()
  const signOut = useMutation({
    mutationFn: api.logout,
    onSuccess: () => {
      void client.cancelQueries()
      client.removeQueries({ predicate: query => query.queryKey[0] !== 'session' })
      client.setQueryData(['session'], null)
    },
  })
  return <Button variant="ghost" aria-label={iconOnly ? 'Sign out' : undefined} title={signOut.isError ? 'Sign out failed. Try again.' : 'Sign out'}
    disabled={signOut.isPending} onClick={() => signOut.mutate()}><LogOut size={16} />{iconOnly ? null : 'Sign out'}
  </Button>
}
