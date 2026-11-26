import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { api, ApiError } from '../shared/api'
import type { ConfigurationView } from '../shared/types'
import { ConfigurationSettings } from './ConfigurationSettings'

const property = { id: 3, name: 'Fixture property', timeZone: 'Europe/Sofia' }
const data: ConfigurationView = { canEdit: true, keyAvailable: true, providers: [{
  provider: 'growatt', revision: 2, pollSeconds: 300, minimumPollSeconds: 300, maximumPollSeconds: 1800,
  pollStepSeconds: 1, credentials: { apiToken: true }, identifiers: {}, pendingIdentifierConversion: [],
}] }

function show() {
  render(<QueryClientProvider client={new QueryClient()}><ConfigurationSettings property={property} /></QueryClientProvider>)
}

afterEach(() => vi.restoreAllMocks())

it('renders presence only and submits blank or explicit remove without leaking existing credentials', async () => {
  vi.spyOn(api, 'configuration').mockImplementation(async property => {
    if (property === null) throw new ApiError(404)
    return data
  })
  const save = vi.spyOn(api, 'saveConfiguration').mockResolvedValue(undefined)
  show()
  const form = within(await screen.findByRole('form', { name: 'Growatt settings' }))
  const password = form.getByLabelText('API token')
  expect(password).toHaveValue('')
  expect(password).toHaveAttribute('type', 'password')
  expect(form.getByText('Configured')).toBeInTheDocument()
  expect(screen.queryByRole('heading', { name: 'Global configuration' })).not.toBeInTheDocument()
  fireEvent.click(form.getByRole('button', { name: 'Save staged settings' }))
  await screen.findByText('Staged settings saved.')
  expect(save).toHaveBeenLastCalledWith(3, 'growatt', { revision: 2, pollSeconds: 300, replacements: {}, remove: [], identifiers: {} })
  fireEvent.click(form.getByLabelText('Remove stored API token'))
  fireEvent.click(form.getByRole('button', { name: 'Save staged settings' }))
  await waitFor(() => expect(save).toHaveBeenLastCalledWith(3, 'growatt',
    { revision: 2, pollSeconds: 300, replacements: {}, remove: ['apiToken'], identifiers: {} }))
})

it('shows saved identifiers and preserves them on a polling edit without an encryption key', async () => {
  vi.spyOn(api, 'configuration').mockImplementation(async property => {
    if (property === null) throw new ApiError(404)
    return { ...data, keyAvailable: false, providers: [{ ...data.providers[0], provider: 'daikin',
      pollStepSeconds: 60, credentials: { clientSecret: true }, identifiers: { clientId: 'saved-client-id' } }] }
  })
  const save = vi.spyOn(api, 'saveConfiguration').mockResolvedValue(undefined)
  show()
  const form = within(await screen.findByRole('form', { name: 'Daikin ONECTA settings' }))
  expect(form.getByLabelText('Client ID')).toHaveValue('saved-client-id')
  expect(form.getByLabelText('Client ID')).toHaveAttribute('type', 'text')
  expect(form.getByLabelText('Client secret')).toBeDisabled()
  expect(form.getByLabelText('Client secret')).toHaveValue('')
  fireEvent.change(form.getByLabelText('Polling interval (seconds)'), { target: { value: '360' } })
  fireEvent.click(form.getByRole('button', { name: 'Save staged settings' }))
  await waitFor(() => expect(save).toHaveBeenCalledWith(3, 'daikin',
    { revision: 2, pollSeconds: 360, replacements: {}, remove: [], identifiers: { clientId: 'saved-client-id' } }))
})

it('flags encrypted legacy identifiers and omits them from submissions', async () => {
  vi.spyOn(api, 'configuration').mockImplementation(async property => {
    if (property === null) throw new ApiError(404)
    return { ...data, providers: [{ ...data.providers[0], provider: 'tuya', pollSeconds: 1800, minimumPollSeconds: 1800,
      pollStepSeconds: 60, credentials: { accessSecret: false }, identifiers: { accessId: null, userId: 'fixture-user' },
      pendingIdentifierConversion: ['accessId'] }] }
  })
  const save = vi.spyOn(api, 'saveConfiguration').mockResolvedValue(undefined)
  show()
  const form = within(await screen.findByRole('form', { name: 'Smart Life / Tuya settings' }))
  expect(form.getByLabelText('Access ID')).toBeDisabled()
  expect(form.getByLabelText('Access ID')).toHaveAccessibleDescription(/must convert/)
  fireEvent.change(form.getByLabelText('Linked user ID'), { target: { value: 'edited-user' } })
  fireEvent.click(form.getByRole('button', { name: 'Save staged settings' }))
  await waitFor(() => expect(save).toHaveBeenCalledWith(3, 'tuya',
    { revision: 2, pollSeconds: 1800, replacements: {}, remove: [], identifiers: { userId: 'edited-user' } }))
})

it('shows installation-wide accounts stored globally as configured on a property page without revealing values', async () => {
  // The reported state: Growatt in property 3, Daikin, eWeLink and Tuya stored only in the global scope.
  const profile = (provider: string, slot: string, ids: string[], global: boolean) => ({
    ...data.providers[0], provider, revision: 0, credentials: { [slot]: false },
    identifiers: Object.fromEntries(ids.map(id => [id, null])), installationWide: true,
    global: global ? { revision: 1, credentials: { [slot]: true }, identifiers: Object.fromEntries(ids.map(id => [id, true])) } : null,
  })
  vi.spyOn(api, 'configuration').mockImplementation(async property => {
    if (property === null) throw new ApiError(404)
    return { ...data, providers: [
      { ...data.providers[0], installationWide: false, global: null },
      profile('daikin', 'clientSecret', ['clientId'], true),
      profile('ewelink', 'appSecret', ['appId'], true),
      profile('tuya', 'accessSecret', ['accessId', 'userId'], false),
    ] }
  })
  show()
  const growatt = (await screen.findByRole('heading', { name: 'Growatt' })).closest('.monitor-grid-item') as HTMLElement
  expect(within(growatt).getByText('Staged configuration complete')).toBeInTheDocument()

  const ewelink = screen.getByRole('heading', { name: 'eWeLink' }).closest('.monitor-grid-item') as HTMLElement
  expect(within(ewelink).getByText('Installation-wide profile complete')).toBeInTheDocument()
  const stored = ewelink.querySelector('dl[aria-label="eWeLink installation-wide profile"]') as HTMLElement
  expect(stored).not.toBeNull()
  expect(within(stored).getByText('App secret').nextElementSibling).toHaveTextContent('Configured')
  expect(within(stored).getByText('App ID').nextElementSibling).toHaveTextContent('Configured')
  expect(within(ewelink).getByText(/does not replace it/)).toBeInTheDocument()
  expect(within(ewelink).getByText('Not staged for this property')).toBeInTheDocument()
  expect(within(ewelink).queryByText('Staged configuration incomplete')).not.toBeInTheDocument()
  expect(within(ewelink).getByLabelText('App ID')).toHaveValue('')

  const daikin = screen.getByRole('heading', { name: 'Daikin ONECTA' }).closest('.monitor-grid-item') as HTMLElement
  expect(within(daikin).getByText('Installation-wide profile complete')).toBeInTheDocument()
  const tuya = screen.getByRole('heading', { name: 'Smart Life / Tuya' }).closest('.monitor-grid-item') as HTMLElement
  expect(within(tuya).getByText('No installation-wide profile')).toBeInTheDocument()
  expect(within(tuya).getByText(/no global profile is stored/)).toBeInTheDocument()
})

it('keeps the global configuration section unchanged for the installation administrator', async () => {
  vi.spyOn(api, 'configuration').mockImplementation(async property => property === null
    ? { ...data, providers: [{ ...data.providers[0], provider: 'ewelink', credentials: { appSecret: true },
      identifiers: { appId: 'global-app' }, installationWide: true, global: null }] }
    : { ...data, providers: [] })
  show()
  const section = (await screen.findByRole('heading', { name: 'Global configuration' })).closest('section') as HTMLElement
  expect(within(section).getByText('Staged configuration complete')).toBeInTheDocument()
  expect(within(section).getByLabelText('App ID')).toHaveValue('global-app')
  expect(within(section).queryByText(/installation-wide account/)).not.toBeInTheDocument()
})

it('keeps viewer configuration read-only', async () => {
  vi.spyOn(api, 'configuration').mockImplementation(async property => {
    if (property === null) throw new ApiError(404)
    return { ...data, canEdit: false, keyAvailable: false }
  })
  show()
  const form = within(await screen.findByRole('form', { name: 'Growatt settings' }))
  expect(form.getByLabelText('Polling interval (seconds)')).toBeDisabled()
  expect(form.queryByLabelText('API token')).not.toBeInTheDocument()
  expect(form.queryByRole('button', { name: 'Save staged settings' })).not.toBeInTheDocument()
})
