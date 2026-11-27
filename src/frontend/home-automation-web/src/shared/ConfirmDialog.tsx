import { useEffect, useId, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import { Button, Input } from './ui'

export type ConfirmDialogProps = {
  open: boolean
  title: string
  /** Consequences shown above the actions. */
  children?: ReactNode
  confirmLabel: string
  /** Destructive actions use a warning style. */
  destructive?: boolean
  /** When set, the confirm button stays disabled until this exact text is typed. */
  typedConfirmation?: string
  typedLabel?: ReactNode
  /** Runs the action; a rejection keeps the dialog open and shows the error. The dialog closes only after it resolves. */
  onConfirm: () => Promise<void>
  onCancel: () => void
  errorText?: (error: unknown) => string
  /** Keeps the confirm button disabled, for example while the impact review reports a blocking condition. */
  blocked?: boolean
}

/**
 * Reusable confirmation modal for ordinary, destructive and typed confirmations. It traps focus, closes on Escape while idle,
 * restores focus to the opener, and ignores repeated submissions while the action runs.
 */
export function ConfirmDialog({ open, title, children, confirmLabel, destructive = false, typedConfirmation, typedLabel,
  onConfirm, onCancel, errorText, blocked = false }: ConfirmDialogProps) {
  const titleId = useId()
  const inputId = useId()
  const panel = useRef<HTMLDivElement>(null)
  const opener = useRef<Element | null>(null)
  const running = useRef(false)
  const [typed, setTyped] = useState('')
  const [pending, setPending] = useState(false)
  const [error, setError] = useState('')

  useEffect(() => {
    if (!open) return
    opener.current = document.activeElement
    setTyped('')
    setError('')
    const first = panel.current?.querySelector<HTMLElement>('input, button')
    first?.focus()
    return () => { if (opener.current instanceof HTMLElement) opener.current.focus() }
  }, [open])

  if (!open) return null
  const matches = !blocked && (typedConfirmation === undefined || typed === typedConfirmation)

  async function confirm() {
    if (running.current || !matches) return
    running.current = true
    setPending(true)
    setError('')
    try {
      await onConfirm()
    } catch (failure) {
      setError(errorText?.(failure) ?? (failure instanceof Error && failure.message ? failure.message : 'The action failed. Try again.'))
    } finally {
      running.current = false
      setPending(false)
    }
  }

  function keyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (event.key === 'Escape' && !pending) {
      event.stopPropagation()
      onCancel()
    }
    if (event.key !== 'Tab' || !panel.current) return
    const focusable = [...panel.current.querySelectorAll<HTMLElement>('input:not(:disabled), button:not(:disabled), select:not(:disabled), a[href]')]
    if (focusable.length === 0) return
    const first = focusable[0]
    const last = focusable[focusable.length - 1]
    if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus() }
    else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus() }
  }

  return <div className="dialog-backdrop">
    <div ref={panel} className="dialog card" role="dialog" aria-modal="true" aria-labelledby={titleId} onKeyDown={keyDown}>
      <h2 id={titleId}>{title}</h2>
      <div className="dialog-body">{children}</div>
      {typedConfirmation !== undefined && <label htmlFor={inputId} className="dialog-typed">
        {typedLabel ?? <>Type <strong>{typedConfirmation}</strong> to confirm</>}
        <Input id={inputId} value={typed} autoComplete="off" spellCheck={false} disabled={pending}
          onChange={event => setTyped(event.target.value)}
          onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); void confirm() } }} />
      </label>}
      {error && <p role="alert" className="form-error">{error}</p>}
      <div className="monitor-actions dialog-actions">
        <Button variant="ghost" type="button" disabled={pending} onClick={onCancel}>Cancel</Button>
        <Button type="button" className={destructive ? 'button-danger' : undefined} disabled={!matches || pending}
          onClick={() => void confirm()}>{pending ? 'Working…' : confirmLabel}</Button>
      </div>
    </div>
  </div>
}
