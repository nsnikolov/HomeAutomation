import { useId } from 'react'

/** A per-instance id that is safe inside `url(#…)` references, so several illustrations on one page never share gradients. */
export function useSvgId(prefix: string) {
  return `${prefix}-${useId().replace(/[^a-zA-Z0-9_-]/g, '')}`
}
