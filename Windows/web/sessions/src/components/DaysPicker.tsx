import { useState } from 'react'

// Which days a check of the LMS reads: a whole month takes minutes, so it is chosen each time.
export interface DayRange { from: string; to: string; label: string }

const iso = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
const back = (days: number) => { const d = new Date(); d.setDate(d.getDate() - days); return iso(d) }
const PRESETS = [
  { key: '1', label: 'Today', days: 0 },
  { key: '3', label: '3 days', days: 2 },
  { key: '7', label: '7 days', days: 6 },
  { key: '14', label: '14 days', days: 13 },
  { key: '30', label: '30 days', days: 29 },
]

export function rangeOf(key: string, from?: string, to?: string): DayRange {
  const preset = PRESETS.find((p) => p.key === key)
  if (preset) return { from: back(preset.days), to: iso(new Date()), label: preset.label }
  return { from: from || back(6), to: to || iso(new Date()), label: `${from} → ${to}` }
}

export function DaysPicker({ value, onChange }: { value: DayRange & { key: string }; onChange: (range: DayRange & { key: string }) => void }) {
  const [from, setFrom] = useState(value.from)
  const [to, setTo] = useState(value.to)
  return (
    <div className="days" role="group" aria-label="Days to check">
      <span className="days-label">Days</span>
      {PRESETS.map((p) => (
        <button key={p.key} type="button" className={`chip small${value.key === p.key ? ' on' : ''}`} onClick={() => onChange({ key: p.key, ...rangeOf(p.key) })}>{p.label}</button>
      ))}
      <button type="button" className={`chip small${value.key === 'custom' ? ' on' : ''}`} onClick={() => onChange({ key: 'custom', ...rangeOf('custom', from, to) })}>From – to</button>
      {value.key === 'custom' && (
        <span className="days-range">
          <input type="date" value={from} max={to} onChange={(e) => { setFrom(e.target.value); onChange({ key: 'custom', ...rangeOf('custom', e.target.value, to) }) }} aria-label="From" />
          <span>→</span>
          <input type="date" value={to} min={from} onChange={(e) => { setTo(e.target.value); onChange({ key: 'custom', ...rangeOf('custom', from, e.target.value) }) }} aria-label="To" />
        </span>
      )}
    </div>
  )
}
