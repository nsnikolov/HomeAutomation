import { useId, useState, type FormEvent } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api, ApiError } from '../shared/api'
import type { Property, ProviderConfigurationView } from '../shared/types'
import { Badge, Button, Card, Input } from '../shared/ui'

const providerNames: Record<string, string> = {
  growatt: 'Growatt', daikin: 'Daikin ONECTA', ewelink: 'eWeLink', tuya: 'Smart Life / Tuya',
}
const fieldNames: Record<string, string> = {
  apiToken: 'API token', clientId: 'Client ID', clientSecret: 'Client secret', appId: 'App ID',
  appSecret: 'App secret', accessId: 'Access ID', accessSecret: 'Access secret', userId: 'Linked user ID',
}

export function ConfigurationSettings({ property }: { property: Property }) {
  return <>
    <div className="page-heading"><h1>Settings</h1></div>
    <p className="muted">Vault settings take effect after restart for providers configured to use the vault.</p>
    <ConfigurationScope property={property.id} title={property.name} />
    <ConfigurationScope property={null} title="Global configuration" />
  </>
}

function ConfigurationScope({ property, title }: { property: number | null; title: string }) {
  const query = useQuery({
    queryKey: ['configuration', property],
    queryFn: ({ signal }) => api.configuration(property, signal),
    retry: false,
  })
  if (query.error instanceof ApiError && query.error.status === 404 && property === null) return null
  if (query.isPending) return <p role="status">Loading configuration…</p>
  if (query.isError) return <p className="notice" role="alert">
    {query.error instanceof ApiError && query.error.status === 503
      ? 'Configuration storage is unavailable. Check migration 015 and backend setup.'
      : 'Configuration could not be loaded.'}
    {' '}<Button variant="outline" onClick={() => void query.refetch()}>Retry</Button>
  </p>
  const { canEdit, keyAvailable, providers, requiresHttps } = query.data
  return <section>
    <h2>{title}</h2>
    {requiresHttps && <p className="notice">Open Settings over HTTPS to edit.</p>}
    {canEdit && !keyAvailable && <p className="notice">Credential replacement requires an available external encryption key.</p>}
    <div className="monitor-grid monitor-grid-widgets">
      {providers.map(profile => <ProviderSettings key={profile.provider} profile={profile} property={property}
        canEdit={canEdit} keyAvailable={keyAvailable} refresh={async () => { await query.refetch() }} />)}
    </div>
  </section>
}

function ProviderSettings({ profile, property, canEdit, keyAvailable, refresh }: {
  profile: ProviderConfigurationView; property: number | null; canEdit: boolean; keyAvailable: boolean
  refresh: () => Promise<void>
}) {
  const id = useId()
  const [pending, setPending] = useState(false)
  const [result, setResult] = useState<{ ok: boolean; text: string }>()
  const name = providerNames[profile.provider] ?? profile.provider
  const slots = Object.entries(profile.credentials)
  const identifiers = Object.entries(profile.identifiers)
  const provisioned = slots.every(([, configured]) => configured) && identifiers.every(([, value]) => Boolean(value))
  // On a property page, an installation-wide account (Daikin, eWeLink, Tuya) lives in the global profile. Property fields stage a
  // separate profile that does not replace it, so the card's status reflects the global profile's presence flags.
  const installationWide = property !== null && profile.installationWide === true
  const global = property !== null ? profile.global ?? null : null
  const globalComplete = global !== null && Object.values(global.credentials).every(Boolean) && Object.values(global.identifiers).every(Boolean)
  const status = !installationWide ? (provisioned ? 'Staged configuration complete' : 'Staged configuration incomplete')
    : globalComplete ? 'Installation-wide profile complete' : global ? 'Installation-wide profile incomplete' : 'No installation-wide profile'

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (pending || !canEdit) return
    const form = event.currentTarget
    const data = new FormData(form)
    const replacements: Record<string, string> = {}
    const submittedIdentifiers: Record<string, string> = {}
    const remove: string[] = []
    for (const [slot] of slots) {
      const secret = data.get(slot)
      if (typeof secret === 'string' && secret.trim()) replacements[slot] = secret
      if (data.has(`remove-${slot}`)) remove.push(slot)
    }
    for (const [field] of identifiers) {
      const value = data.get(field)
      if (typeof value === 'string') submittedIdentifiers[field] = value
    }
    setPending(true)
    setResult(undefined)
    try {
      await api.saveConfiguration(property, profile.provider,
        { revision: profile.revision, pollSeconds: Number(data.get('pollSeconds')), replacements, remove,
          identifiers: submittedIdentifiers })
      await refresh()
      setResult({ ok: true, text: 'Staged settings saved.' })
    } catch (error) {
      const conflict = error instanceof ApiError && error.status === 409
      if (conflict) await refresh()
      setResult({ ok: false, text: conflict
        ? 'These settings changed. Review the latest values before saving again.'
        : error instanceof ApiError && error.status === 426 ? 'Open Settings over HTTPS to save changes.'
          : 'Settings could not be saved. Check the input and configuration availability.' })
    } finally {
      form.reset()
      setPending(false)
    }
  }

  return <Card className="monitor-grid-item" aria-labelledby={id}>
    <h3 id={id}>{name}</h3>
    <Badge>{status}</Badge>
    {global && <dl className="device-readings" aria-label={`${name} installation-wide profile`}>
      {[...Object.entries(global.identifiers), ...Object.entries(global.credentials)].map(([field, present]) => <div key={field}>
        <dt>{fieldNames[field] ?? field}</dt><dd>{present ? 'Configured' : 'Not configured'}</dd>
      </div>)}
    </dl>}
    {installationWide && <p className="muted">{global
      ? `${name} uses one installation-wide account, shown above. The fields below stage a separate profile for this property, which does not replace it.`
      : `${name} uses one installation-wide account, and no global profile is stored. An installation administrator imports or edits it in the global configuration.`}</p>}
    <form aria-label={`${name} settings`} onSubmit={submit} className="settings-form" key={profile.revision}>
      <fieldset disabled={!canEdit || pending}>
        <label htmlFor={`${id}-interval`}>Polling interval (seconds)</label>
        <Input id={`${id}-interval`} name="pollSeconds" type="number" required min={profile.minimumPollSeconds}
          max={profile.maximumPollSeconds} step={profile.pollStepSeconds} defaultValue={profile.pollSeconds} />
        {identifiers.map(([field, value]) => {
          const needsConversion = profile.pendingIdentifierConversion.includes(field)
          return <div key={field}>
            <label htmlFor={`${id}-${field}`}>{fieldNames[field] ?? field}</label>
            <Input id={`${id}-${field}`} name={field} type="text" defaultValue={value ?? ''}
              maxLength={4096} disabled={needsConversion} aria-describedby={needsConversion ? `${id}-${field}-status` : undefined} />
            {needsConversion && <span id={`${id}-${field}-status`} className="muted">
              Saved in the previous format. An administrator must convert it before editing.
            </span>}
          </div>
        })}
        {slots.map(([slot, configured]) => <div key={slot}>
          <label htmlFor={`${id}-${slot}`}>{fieldNames[slot] ?? slot}</label>
          {' '}<Badge>{configured ? 'Configured' : installationWide ? 'Not staged for this property' : 'Not configured'}</Badge>
          {canEdit && <Input id={`${id}-${slot}`} name={slot} type="password" disabled={!keyAvailable}
            autoComplete="new-password" maxLength={4096} />}
          {canEdit && configured && <label>
            <input name={`remove-${slot}`} type="checkbox" /> Remove stored {fieldNames[slot] ?? slot}
          </label>}
        </div>)}
        {canEdit && <Button type="submit" variant="outline">{pending ? 'Saving…' : 'Save staged settings'}</Button>}
      </fieldset>
    </form>
    {result && <p role={result.ok ? 'status' : 'alert'}>{result.text}</p>}
  </Card>
}
