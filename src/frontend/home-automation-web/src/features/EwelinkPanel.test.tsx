import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { expect, it, vi } from 'vitest'
import { api } from '../shared/api'
import { EwelinkPanel } from './EwelinkPanel'

const property = { id: 1, name: 'Fixture', timeZone: 'Europe/Sofia' }

function view(path = '/') {
  return render(<QueryClientProvider client={new QueryClient()}>
    <MemoryRouter initialEntries={[path]}><EwelinkPanel property={property} /></MemoryRouter>
  </QueryClientProvider>)
}

it('finishes an OAuth return with a local action and clears the finish marker', async () => {
  const status = vi.spyOn(api, 'ewelink').mockResolvedValue({
    status: 'disconnected', manage: true, lastSuccess: null, nextSync: null, devices: [], unassigned: [],
  })
  const rooms = vi.spyOn(api, 'rooms').mockResolvedValue([])
  const complete = vi.spyOn(api, 'updateEwelink').mockResolvedValue(undefined)
  try {
    view('/properties/1/ewelink?connect=finish')
    const finish = await screen.findByRole('button', { name: 'Finish connecting' })
    expect(complete).not.toHaveBeenCalled()
    fireEvent.click(finish)
    await waitFor(() => expect(complete).toHaveBeenCalledWith(1, 'complete'))
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Finish connecting' })).not.toBeInTheDocument())
  } finally {
    status.mockRestore()
    rooms.mockRestore()
    complete.mockRestore()
  }
})

it('shows the approval boundary and hides account actions without configuration', async () => {
  const status = vi.spyOn(api, 'ewelink').mockResolvedValue({
    status: 'notConfigured', manage: false, lastSuccess: null, nextSync: null, devices: [], unassigned: [],
  })
  try {
    view()
    expect(await screen.findByText(/needs approved Developer credentials/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Connect account' })).not.toBeInTheDocument()
  } finally { status.mockRestore() }
})

it('shows offline and last-known state without offering physical controls', async () => {
  const timestamp = '2026-11-22T12:00:00Z'
  const status = vi.spyOn(api, 'ewelink').mockResolvedValue({
    status: 'partial', manage: false, lastSuccess: timestamp, nextSync: null, unassigned: [],
    devices: [{ id: 'fixture', name: 'Test relay', model: 'Fixture', roomId: 1, connectivity: 'offline',
      seenAt: timestamp, quality: 'fresh', hasRelay: true, relay: true, relayAt: timestamp, relayQuality: 'stale' }],
  })
  try {
    view()
    expect(await screen.findByRole('heading', { name: 'Test relay' })).toBeInTheDocument()
    expect(screen.getByText('Offline')).toBeInTheDocument()
    expect(screen.getByText('On · Last known')).toBeInTheDocument()
    expect(screen.getAllByText('Unavailable')).toHaveLength(2)
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  } finally { status.mockRestore() }
})
