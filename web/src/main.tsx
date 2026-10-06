import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { apiScopes, msal } from './auth.ts'

async function start() {
  await msal.initialize()
  // Finishes a sign-in redirect if we're coming back from one.
  const result = await msal.handleRedirectPromise()
  const account = result?.account ?? msal.getAllAccounts()[0]

  if (!account) {
    // Asking for the API scope here means the first silent token request succeeds.
    await msal.loginRedirect({ scopes: apiScopes })
    return
  }

  msal.setActiveAccount(account)
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <App />
    </StrictMode>,
  )
}

start().catch((e: Error) => {
  // Without this, a sign-in setup error leaves a blank page.
  document.getElementById('root')!.textContent = `Sign-in failed: ${e.message}`
})
