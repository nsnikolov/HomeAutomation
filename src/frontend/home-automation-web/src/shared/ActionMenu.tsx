import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react'
import { MoreHorizontal } from 'lucide-react'

export type ActionMenuItem = { label: string; onSelect: () => void; destructive?: boolean; disabled?: boolean }

/** A compact, keyboard-accessible three-dot menu for secondary actions. */
export function ActionMenu({ label, items }: { label: string; items: ActionMenuItem[] }) {
  const [open, setOpen] = useState(false)
  const menuId = useId()
  const trigger = useRef<HTMLButtonElement>(null)
  const root = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    root.current?.querySelector<HTMLButtonElement>('[role="menuitem"]:not(:disabled)')?.focus()
    const outside = (event: MouseEvent) => { if (!root.current?.contains(event.target as Node)) setOpen(false) }
    document.addEventListener('mousedown', outside)
    return () => document.removeEventListener('mousedown', outside)
  }, [open])

  function close() {
    setOpen(false)
    trigger.current?.focus()
  }

  function keyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (!open) return
    if (event.key === 'Escape') { event.preventDefault(); close(); return }
    if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return
    event.preventDefault()
    const entries = [...(root.current?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]:not(:disabled)') ?? [])]
    const index = entries.indexOf(document.activeElement as HTMLButtonElement)
    entries[(index + (event.key === 'ArrowDown' ? 1 : -1) + entries.length) % entries.length]?.focus()
  }

  return <div className="action-menu" ref={root} onKeyDown={keyDown}>
    <button ref={trigger} type="button" className="action-menu-trigger" aria-label={label} aria-haspopup="menu" aria-expanded={open}
      aria-controls={open ? menuId : undefined} onClick={() => setOpen(!open)}>
      <MoreHorizontal size={18} aria-hidden />
    </button>
    {open && <ul id={menuId} role="menu" aria-label={label} className="action-menu-list">
      {items.map(item => <li key={item.label} role="none">
        <button type="button" role="menuitem" disabled={item.disabled} className={item.destructive ? 'action-menu-danger' : undefined}
          onClick={() => { setOpen(false); item.onSelect() }}>{item.label}</button>
      </li>)}
    </ul>}
  </div>
}
