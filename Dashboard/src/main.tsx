import { QueryCache, QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router'
import { UnauthorizedError } from './api/client'
import { App } from './App'
import { ToastProvider } from './components/Toast'
import './index.css'

// Any request that finds the session gone signs the page out, which sends it to the login page.
const queryClient: QueryClient = new QueryClient({
  queryCache: new QueryCache({
    onError: (error) => {
      if (error instanceof UnauthorizedError) queryClient.setQueryData(['me'], null)
    },
  }),
  defaultOptions: {
    queries: {
      retry: (failures, error) => !(error instanceof UnauthorizedError) && failures < 2,
      refetchOnWindowFocus: true,
    },
  },
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <BrowserRouter basename="/dashboard">
        <ToastProvider>
          <App />
        </ToastProvider>
      </BrowserRouter>
    </QueryClientProvider>
  </StrictMode>,
)
