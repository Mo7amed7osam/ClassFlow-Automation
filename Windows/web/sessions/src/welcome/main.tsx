import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { Welcome } from './Welcome'
import '../styles.css'
import './welcome.css'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Welcome />
  </StrictMode>,
)
