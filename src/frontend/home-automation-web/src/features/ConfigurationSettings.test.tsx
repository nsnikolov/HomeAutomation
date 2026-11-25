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
