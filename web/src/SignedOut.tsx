import './App.css'
import { signIn } from './auth.ts'

export default function SignedOut() {
  return (
    <main className="page landing">
      <h1>API Reliability Dashboard</h1>
      <p className="muted">
        Checks your HTTP endpoints every minute and charts their status and latency.
      </p>
      <button type="button" className="primary" onClick={() => signIn()}>
        Sign in
      </button>
    </main>
  )
}
