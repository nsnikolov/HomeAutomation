import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { api, ApiError } from '../shared/api'
import type { ConfigurationView } from '../shared/types'
import { ConfigurationSettings } from './ConfigurationSettings'

const property = { id: 3, name: 'Fixture property', timeZone: 'Europe/Sofia' }
const data: ConfigurationView = { canEdit: true, keyAvailable: true, providers: [{
  provider: 'growatt', revision: 2, pollSeconds: 300, minimumPollSeconds: 300, maximumPollSeconds: 1800,
  pollStepSeconds: 1, credentials: { apiToken: true },
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
  expect(save).toHaveBeenLastCalledWith(3, 'growatt', { revision: 2, pollSeconds: 300, replacements: {}, remove: [] })
  fireEvent.click(form.getByLabelText('Remove stored API token'))
  fireEvent.click(form.getByRole('button', { name: 'Save staged settings' }))
  await waitFor(() => expect(save).toHaveBeenLastCalledWith(3, 'growatt',
    { revision: 2, pollSeconds: 300, replacements: {}, remove: ['apiToken'] }))
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
