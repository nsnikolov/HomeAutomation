import { fireEvent, render, screen } from '@testing-library/react'
import { expect, it, vi } from 'vitest'
import { DeviceCardOrder, orderDevices } from './DeviceCardOrder'
import { mergeHistory } from './Energy'

it('keeps previously ordered cards first and keeps filtering out of the stored order', () => {
  const devices = [{ id: 1, name: 'One' }, { id: 2, name: 'Two' }, { id: 3, name: 'Three' }]
  expect(orderDevices(devices, ['2', '1']).map(item => item.id)).toEqual([2, 1, 3])
  const update = vi.fn()
  render(<DeviceCardOrder items={devices} group="assigned" visible={item => item.id !== 2}
    preferences={{ data: { order: { assigned: ['1', '2'] }, hidden: [] }, ready: true, loading: false, busy: false, error: '', update }}>
    {device => <>{device.name}<button type="button">Action</button></>}
  </DeviceCardOrder>)
  fireEvent.keyDown(screen.getByRole('button', { name: 'Reorder Three' }), { key: 'ArrowUp' })
  expect(update).toHaveBeenCalledWith({ group: 'assigned', ids: ['3', '2', '1'] })
  fireEvent.click(screen.getAllByRole('button', { name: 'Action' })[0])
  expect(update).toHaveBeenCalledTimes(1)
})

it('inserts a display gap between widely separated reports', () => {
  const at = (time: string, number: number) => ({ observedAt: time, number, flag: null, code: null })
  const data = mergeHistory([{ metric: 'temperature', points: [
    at('2026-11-22T12:00:00Z', 20), at('2026-11-22T16:00:00Z', 21),
  ] }], 1800)
  expect(data.map(point => point.temperature)).toEqual([20, null, 21])
})

it('drops a touch-drag only inside the original collection', () => {
  const update = vi.fn()
  const preferences = { data: { order: {}, hidden: [] }, ready: true, loading: false, busy: false, error: '', update }
  render(<>
    <DeviceCardOrder items={[{ id: 1, name: 'First' }, { id: 2, name: 'Second' }]} group="assigned" preferences={preferences}>
      {device => <span>{device.name}</span>}</DeviceCardOrder>
    <DeviceCardOrder items={[{ id: 3, name: 'Candidate' }]} group="unassigned" preferences={preferences}>
      {device => <span>{device.name}</span>}</DeviceCardOrder>
  </>)
  const handle = screen.getByRole('button', { name: 'Reorder First' })
  Object.defineProperty(handle, 'setPointerCapture', { value: vi.fn() })
  const hitTest = vi.fn().mockReturnValueOnce(screen.getByText('Candidate')).mockReturnValueOnce(screen.getByText('Second'))
  const original = Object.getOwnPropertyDescriptor(document, 'elementFromPoint')
  Object.defineProperty(document, 'elementFromPoint', { configurable: true, value: hitTest })
  try {
    const touch = { pointerId: 1, button: 0, pointerType: 'touch', isPrimary: true }
    fireEvent.pointerDown(handle, touch)
    fireEvent.pointerUp(handle, touch)
    expect(update).not.toHaveBeenCalled()
    fireEvent.pointerDown(handle, touch)
    fireEvent.pointerUp(handle, touch)
    expect(update).toHaveBeenCalledWith({ group: 'assigned', ids: ['2', '1'] })
  } finally {
    if (original) Object.defineProperty(document, 'elementFromPoint', original)
    else Reflect.deleteProperty(document, 'elementFromPoint')
  }
})
