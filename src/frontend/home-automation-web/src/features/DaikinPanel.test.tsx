import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { expect, it } from 'vitest'
import { DaikinDeviceCard } from './DaikinPanel'
import type { DaikinDevice } from '../shared/types'

const device: DaikinDevice = {
  id: 'fixture', name: 'Upstairs', model: 'Fixture', roomId: null, connectivity: 'online', quality: 'fresh',
  observedAt: '2026-11-21T12:00:00Z', sourceAt: '2026-11-21T12:00:00Z', power: 'on', mode: 'heating',
  indoor: 18, target: 16, outdoor: null, humidity: null, fan: null, fanMode: null, fault: null, command: null,
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
  expect(screen.getByLabelText('Target temperature command')).toHaveAttribute('min', '16')
  expect(screen.queryByLabelText('Fixed fan speed command')).not.toBeInTheDocument()
})

it('blocks stale and uncertain writes while showing desired state separately', () => {
  view({ ...device, quality: 'stale', command: {
    id: 'audit', operation: 'temperature', value: '20', status: 'uncertain', updatedAt: device.observedAt,
  } }, true)
  expect(screen.getByRole('button', { name: 'Set target temperature' })).toBeDisabled()
  expect(screen.getByText('16 °C')).toBeInTheDocument()
  expect(screen.getByText(/Outcome uncertain.*20/)).toBeInTheDocument()
})
