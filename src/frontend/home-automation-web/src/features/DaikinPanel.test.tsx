import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { expect, it, vi } from 'vitest'
import { DaikinDeviceCard } from './DaikinPanel'
import type { DaikinDevice } from '../shared/types'
import { api } from '../shared/api'

const device: DaikinDevice = {
  id: 'fixture', name: 'Upstairs', model: 'Fixture', roomId: null, connectivity: 'online', quality: 'fresh',
  observedAt: '2026-11-21T12:00:00Z', sourceAt: '2026-11-21T12:00:00Z', power: 'on', mode: 'heating',
  indoor: 18, target: 16, outdoor: null, humidity: null, fan: null, fanMode: null, fault: null, command: null,
  equipment: 'airConditioner', water: null,
  controls: { power: ['on', 'off'], modes: ['heating', 'cooling'], temperature: { min: 16, max: 24, step: 0.5 }, fan: null },
}
const property = { id: 1, name: 'Property', timeZone: 'Europe/Sofia' }

function view(unit: DaikinDevice, writes: boolean) {
  return render(<QueryClientProvider client={new QueryClient()}>
    <DaikinDeviceCard device={unit} property={property} writes={writes} />
  </QueryClientProvider>)
}

it('keeps controls disabled in read-only mode and hides unsupported fan control', () => {
  view(device, false)
  expect(screen.getByText('18 °C')).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Off' })).toBeDisabled()
  expect(screen.getByRole('button', { name: 'Raise target temperature' })).toBeDisabled()
  expect(screen.queryByLabelText('Fixed fan speed command')).not.toBeInTheDocument()
})

it('blocks stale and uncertain writes while showing desired state separately', () => {
  view({ ...device, quality: 'stale', command: {
    id: 'audit', operation: 'temperature', value: '20', status: 'uncertain', updatedAt: device.observedAt,
  } }, true)
  expect(screen.getByRole('button', { name: 'Raise target temperature' })).toBeDisabled()
  expect(screen.getByText('16 °C')).toBeInTheDocument()
  expect(screen.getByText(/Outcome uncertain.*20/)).toBeInTheDocument()
})

it('shows hydronic temperatures and monitoring only even when AC writes are enabled', () => {
  view({ ...device, equipment: 'heatPump', water: {
    leaving: 41, target: 38, targetRange: { min: 25, max: 55, step: 1 }, controlMode: 'leavingWaterTemperature',
    setpointMode: 'fixed', warning: false, emergency: true, providerUnit: 'C', targetAdvertisedWritable: true,
  } }, true)
  expect(screen.getByText('41 °C')).toBeInTheDocument()
  expect(screen.getByText('38 °C')).toBeInTheDocument()
  expect(screen.getByText('Active')).toBeInTheDocument()
  expect(screen.queryByText('Indoor temperature')).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Off' })).not.toBeInTheDocument()
})

it('coalesces target edits until Apply and submits only once while awaiting readback', async () => {
  const command = vi.spyOn(api, 'commandDaikin').mockResolvedValue({
    id: 'fixture-command', operation: 'temperature', value: '17', status: 'uncertain', updatedAt: device.observedAt,
  })
  try {
    view(device, true)
    expect(screen.getByRole('button', { name: 'Lower target temperature' })).toBeDisabled()
    const raise = screen.getByRole('button', { name: 'Raise target temperature' })
    fireEvent.click(raise)
    fireEvent.click(raise)
    expect(screen.getByText('Requested target 17 °C')).toBeInTheDocument()
    expect(command).not.toHaveBeenCalled()
    const apply = screen.getByRole('button', { name: 'Apply target' })
    fireEvent.click(apply)
    fireEvent.click(apply)
    await waitFor(() => expect(command).toHaveBeenCalledWith(1, device.id, 'temperature', '17', 'heating'))
    expect(command).toHaveBeenCalledTimes(1)
    expect(await screen.findByText(/Outcome uncertain.*17/)).toBeInTheDocument()
    expect(screen.getByText('16 °C')).toBeInTheDocument()
  } finally {
    command.mockRestore()
  }
})
