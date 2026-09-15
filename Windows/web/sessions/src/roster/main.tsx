import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { Roster } from './Roster'
import '../styles.css'
import './roster.css'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Roster />
  </StrictMode>,
)
