import { createContext, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from 'react'

type Kind = 'success' | 'error'
interface Toast {
  id: number
  kind: Kind
  title: string
  body?: string
}

interface ToastApi {
  success: (title: string, body?: string) => void
  error: (title: string, body?: string) => void
}

const Context = createContext<ToastApi>({ success: () => {}, error: () => {} })

/** Short confirmations in the corner, gone after a few seconds (errors stay a little longer). */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([])
  const next = useRef(1)

  const dismiss = useCallback((id: number) => setToasts((all) => all.filter((t) => t.id !== id)), [])
  const push = useCallback(
    (kind: Kind, title: string, body?: string) => {
      const id = next.current++
      setToasts((all) => [...all.slice(-3), { id, kind, title, body }])
      window.setTimeout(() => dismiss(id), kind === 'error' ? 8000 : 5000)
    },
    [dismiss],
  )
  const api = useMemo<ToastApi>(() => ({ success: (t, b) => push('success', t, b), error: (t, b) => push('error', t, b) }), [push])

  return (
    <Context.Provider value={api}>
      {children}
      <div aria-live="polite" className="pointer-events-none fixed inset-x-3 bottom-3 z-[60] flex flex-col items-end gap-2 sm:inset-x-auto sm:right-5 sm:bottom-5">
        {toasts.map((toast) => (
          <div
            key={toast.id}
            role={toast.kind === 'error' ? 'alert' : 'status'}
            className={`pointer-events-auto w-full max-w-sm rounded-xl border px-4 py-3 text-sm shadow-lg ${
              toast.kind === 'error' ? 'border-rose-200 bg-rose-50 text-rose-800' : 'border-emerald-200 bg-white text-slate-800'
            }`}
          >
            <div className="flex items-start gap-3">
              <span aria-hidden className={`mt-1 size-2 shrink-0 rounded-full ${toast.kind === 'error' ? 'bg-rose-500' : 'bg-emerald-500'}`} />
              <div className="min-w-0 flex-1">
                <p className="font-semibold">{toast.title}</p>
                {toast.body && <p className="mt-0.5 break-words text-slate-600">{toast.body}</p>}
              </div>
              <button type="button" aria-label="Dismiss" onClick={() => dismiss(toast.id)} className="text-slate-400 hover:text-slate-700">×</button>
            </div>
          </div>
        ))}
      </div>
    </Context.Provider>
  )
}

export function useToast(): ToastApi {
  return useContext(Context)
}
