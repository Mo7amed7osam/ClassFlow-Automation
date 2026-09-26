import { useEffect, useId, useRef, type ReactNode, type RefObject } from 'react'
import { createPortal } from 'react-dom'

const FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'

// Overlays can stack (an edit dialog opened from the details drawer): only the top one reacts to keys.
const openOverlays: object[] = []

/**
 * What every overlay needs: Escape closes it (unless busy), Tab stays inside it, the page behind
 * does not scroll, the first field gets focus, and focus goes back where it was on close.
 */
function useOverlay(open: boolean, onClose: () => void, busy: boolean, panel: RefObject<HTMLDivElement | null>) {
  const close = useRef(onClose)
  close.current = onClose
  const blocked = useRef(busy)
  blocked.current = busy

  useEffect(() => {
    if (!open) return
    const self = {}
    openOverlays.push(self)
    const before = document.activeElement as HTMLElement | null
    const overflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    const first = panel.current?.querySelector<HTMLElement>('[data-autofocus]') ?? panel.current?.querySelector<HTMLElement>(FOCUSABLE)
    first?.focus()

    function onKey(event: KeyboardEvent) {
      if (openOverlays[openOverlays.length - 1] !== self) return
      if (event.key === 'Escape' && !blocked.current) {
        event.stopPropagation()
        close.current()
      } else if (event.key === 'Tab' && panel.current) {
        const items = Array.from(panel.current.querySelectorAll<HTMLElement>(FOCUSABLE))
        if (items.length === 0) return
        const [head, tail] = [items[0], items[items.length - 1]]
        if (event.shiftKey && document.activeElement === head) {
          event.preventDefault()
          tail.focus()
        } else if (!event.shiftKey && document.activeElement === tail) {
          event.preventDefault()
          head.focus()
        }
      }
    }
    document.addEventListener('keydown', onKey)
    return () => {
      openOverlays.splice(openOverlays.indexOf(self), 1)
      document.removeEventListener('keydown', onKey)
      document.body.style.overflow = overflow
      before?.focus?.()
    }
  }, [open, panel])
}

interface OverlayProps {
  open: boolean
  title: ReactNode
  onClose: () => void
  children: ReactNode
  footer?: ReactNode
  /** While true, Escape and clicking outside do nothing (a request is running). */
  busy?: boolean
  description?: ReactNode
}

function Header({ id, title, description, onClose, busy }: { id: string; title: ReactNode; description?: ReactNode; onClose: () => void; busy: boolean }) {
  return (
    <header className="flex items-start justify-between gap-4 border-b border-slate-100 dark:border-slate-800/80 px-5 py-4">
      <div>
        <h2 id={id} className="text-base font-semibold text-slate-900 dark:text-slate-100">{title}</h2>
        {description && <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">{description}</p>}
      </div>
      <button type="button" onClick={onClose} disabled={busy} aria-label="Close" className="-m-1 rounded-md p-1 text-slate-400 hover:bg-slate-100 hover:text-slate-700 dark:text-slate-400 dark:hover:bg-slate-800 dark:hover:text-slate-200 disabled:opacity-40">
        <svg viewBox="0 0 20 20" className="size-5" aria-hidden="true"><path d="M5 5l10 10M15 5L5 15" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" /></svg>
      </button>
    </header>
  )
}

/** A centred dialog (a bottom sheet on small screens). */
export function Modal({ open, title, description, onClose, children, footer, busy = false }: OverlayProps) {
  const panel = useRef<HTMLDivElement>(null)
  const id = useId()
  useOverlay(open, onClose, busy, panel)
  if (!open) return null
  return createPortal(
    <div
      className="fixed inset-0 z-50 flex items-end justify-center bg-slate-950/60 backdrop-blur-xs sm:items-center sm:p-4"
      onMouseDown={(event) => event.target === event.currentTarget && !busy && onClose()}
    >
      <div ref={panel} role="dialog" aria-modal="true" aria-labelledby={id} className="flex max-h-[92vh] w-full flex-col overflow-hidden rounded-t-2xl bg-white shadow-2xl sm:max-w-lg sm:rounded-2xl dark:bg-[#111726] dark:border dark:border-slate-800/80 ring-1 ring-slate-900/5 dark:ring-white/[0.03]">
        <Header id={id} title={title} description={description} onClose={onClose} busy={busy} />
        <div className="overflow-y-auto px-5 py-4 text-slate-800 dark:text-slate-200">{children}</div>
        {footer && <footer className="flex flex-wrap justify-end gap-2 border-t border-slate-100 dark:border-slate-800/80 bg-slate-50/60 dark:bg-[#0c111d] px-5 py-3">{footer}</footer>}
      </div>
    </div>,
    document.body,
  )
}

/** A panel sliding in from the right (full screen on small screens). */
export function Drawer({ open, title, description, onClose, children, footer, busy = false }: OverlayProps) {
  const panel = useRef<HTMLDivElement>(null)
  const id = useId()
  useOverlay(open, onClose, busy, panel)
  if (!open) return null
  return createPortal(
    <div className="fixed inset-0 z-40 flex justify-end bg-slate-950/60 backdrop-blur-xs" onMouseDown={(event) => event.target === event.currentTarget && onClose()}>
      <div ref={panel} role="dialog" aria-modal="true" aria-labelledby={id} className="flex h-full w-full flex-col bg-white shadow-2xl sm:max-w-md dark:bg-[#111726] dark:border-l dark:border-slate-800/80">
        <Header id={id} title={title} description={description} onClose={onClose} busy={busy} />
        <div className="flex-1 overflow-y-auto px-5 py-4 text-slate-800 dark:text-slate-200">{children}</div>
        {footer && <footer className="flex flex-wrap justify-end gap-2 border-t border-slate-100 dark:border-slate-800/80 bg-slate-50/60 dark:bg-[#0c111d] px-5 py-3">{footer}</footer>}
      </div>
    </div>,
    document.body,
  )
}
