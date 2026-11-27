import { CircleAlert, Info, OctagonAlert } from 'lucide-react'
import type { Severity, Truth } from '../../shared/alerts'
import { severityLabel, truthLabel } from '../../shared/alerts'
import { Badge } from '../../shared/ui'

const severityTone: Record<Severity, string> = { info: 'tone-neutral', warning: 'tone-warn', critical: 'tone-bad' }
const severityIcon = { info: Info, warning: CircleAlert, critical: OctagonAlert }

/** Severity shown with an icon and its name, never by color alone. */
export function SeverityBadge({ severity }: { severity: Severity }) {
  const Icon = severityIcon[severity]
  return <Badge className={`severity severity-${severity} ${severityTone[severity]}`}><Icon size={13} aria-hidden />{severityLabel[severity]}</Badge>
}

export function TruthBadge({ value }: { value: Truth }) {
  return <Badge className={value === 'true' ? 'tone-bad' : value === 'false' ? 'tone-good' : 'tone-neutral'}>{truthLabel[value]}</Badge>
}
