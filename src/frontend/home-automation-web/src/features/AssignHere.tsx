import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Button } from '../shared/ui'

/**
 * Assigns a provider device that belongs to no property to this property, without a room.
 * Rooms are optional and are changed from the device's menu on the Devices page.
 */
export function AssignHere({ property, assign }: { property: number; assign: () => Promise<unknown> }) {
  const client = useQueryClient()
  const assignment = useMutation({
    mutationFn: assign,
    onSuccess: () => Promise.all([client.invalidateQueries({ queryKey: ['property', property] }),
      client.invalidateQueries({ queryKey: ['property-management'] })]),
  })
  return <div className="filters">
    <Button variant="outline" disabled={assignment.isPending} onClick={() => assignment.mutate()}>
      {assignment.isPending ? 'Assigning…' : 'Assign here'}
    </Button>
    {assignment.isError && <p role="alert">Could not assign this device.</p>}
  </div>
}
