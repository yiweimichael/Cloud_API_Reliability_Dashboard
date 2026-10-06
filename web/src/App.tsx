import type { AccountInfo } from '@azure/msal-browser'
import { type FormEvent, useEffect, useState } from 'react'
import './App.css'
import { getAccessToken, signOut } from './auth.ts'

type Endpoint = { id: number; url: string }

type Check = {
  id: number
  checkTimeUtc: string
  success: boolean
  latencyMs: number
  httpStatusCode: number | null
}

type EndpointWithChecks = Endpoint & { checks: Check[] }

const REFRESH_MS = 30_000

async function apiFetch(url: string, init?: { method: string; body?: unknown }) {
  const token = await getAccessToken()
  const headers: Record<string, string> = { Authorization: `Bearer ${token}` }
  if (init?.body !== undefined) {
    headers['Content-Type'] = 'application/json'
  }
  return fetch(url, {
    method: init?.method,
    headers,
    body: init?.body === undefined ? undefined : JSON.stringify(init.body),
  })
}

// The API answers 400/409 with { error: "..." }; fall back to the status code otherwise.
async function errorMessage(response: Response) {
  const body = await response.json().catch(() => null)
  return body?.error ?? `Request failed (${response.status})`
}

async function getJson<T>(url: string): Promise<T> {
  const response = await apiFetch(url)
  if (!response.ok) {
    throw new Error(`${url} returned ${response.status}`)
  }
  return response.json()
}

async function loadAll(): Promise<EndpointWithChecks[]> {
  const endpoints = await getJson<Endpoint[]>('/endpoints')
  return Promise.all(
    endpoints.map(async (e) => ({
      ...e,
      checks: await getJson<Check[]>(`/endpoints/${e.id}/checks`),
    })),
  )
}

function timeAgo(iso: string, now: number) {
  const seconds = Math.max(0, Math.round((now - Date.parse(iso)) / 1000))
  if (seconds < 60) return `${seconds}s ago`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`
  return `${Math.floor(seconds / 86400)}d ago`
}

function App({ account }: { account: AccountInfo }) {
  const [data, setData] = useState<EndpointWithChecks[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loadedAt, setLoadedAt] = useState(0)
  // Bumped after an add or delete to reload right away instead of waiting for the timer.
  const [reloadKey, setReloadKey] = useState(0)
  const reload = () => setReloadKey((k) => k + 1)

  useEffect(() => {
    // Also drops a load still in flight when a newer one starts, so stale data can't win.
    let cancelled = false
    const refresh = () =>
      loadAll()
        .then((result) => {
          if (cancelled) return
          setData(result)
          setError(null)
          setLoadedAt(Date.now())
        })
        .catch((e: Error) => {
          if (!cancelled) setError(`Failed to load: ${e.message}`)
        })

    refresh()
    const timer = setInterval(refresh, REFRESH_MS)
    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [reloadKey])

  async function remove(endpoint: Endpoint) {
    if (!confirm(`Stop monitoring ${endpoint.url}? Its check history will be deleted too.`)) return
    try {
      const response = await apiFetch(`/endpoints/${endpoint.id}`, { method: 'DELETE' })
      // 404 means it's already gone, which is what we wanted.
      if (!response.ok && response.status !== 404) {
        throw new Error(await errorMessage(response))
      }
      reload()
    } catch (e) {
      setError(`Failed to delete: ${(e as Error).message}`)
    }
  }

  return (
    <main className="page">
      <header className="top">
        <h1>API Reliability Dashboard</h1>
        <div className="account">
          <span className="muted">{account.username || account.name}</span>
          <button type="button" onClick={() => signOut()}>
            Sign out
          </button>
        </div>
      </header>
      <AddEndpointForm onAdded={reload} />
      {error && <p className="error">{error}</p>}
      {!data && !error && <p className="muted">Loading…</p>}
      {data?.length === 0 && <p className="muted">No endpoints yet. Add a URL above to start monitoring it.</p>}
      {data?.map((endpoint) => (
        <section key={endpoint.id} className="card">
          <div className="card-head">
            <p className="url">{endpoint.url}</p>
            <button type="button" className="delete" onClick={() => remove(endpoint)}>
              Delete
            </button>
          </div>
          <Status latest={endpoint.checks[0]} now={loadedAt} />
          <LatencyChart checks={endpoint.checks} />
        </section>
      ))}
    </main>
  )
}

function AddEndpointForm({ onAdded }: { onAdded: () => void }) {
  const [url, setUrl] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const response = await apiFetch('/endpoints', { method: 'POST', body: { url: url.trim() } })
      if (!response.ok) {
        setError(await errorMessage(response))
        return
      }
      setUrl('')
      onAdded()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="add" onSubmit={submit}>
      <input
        type="url"
        required
        placeholder="https://example.com"
        aria-label="Endpoint URL"
        value={url}
        onChange={(e) => setUrl(e.target.value)}
        disabled={busy}
      />
      <button type="submit" className="primary" disabled={busy}>
        {busy ? 'Adding…' : 'Add'}
      </button>
      {error && <p className="error">{error}</p>}
    </form>
  )
}

function Status({ latest, now }: { latest: Check | undefined; now: number }) {
  if (!latest) {
    return <p className="muted">No checks yet.</p>
  }

  return (
    <div className="status">
      <span>
        <span className={`dot ${latest.success ? 'up' : 'down'}`} />
        {latest.success ? 'Up' : 'Down'}
      </span>
      <span>{latest.httpStatusCode ? `HTTP ${latest.httpStatusCode}` : 'No response'}</span>
      <span>{latest.latencyMs} ms</span>
      <span className="muted">checked {timeAgo(latest.checkTimeUtc, now)}</span>
    </div>
  )
}

const W = 600
const H = 120
const PAD = 8

function LatencyChart({ checks }: { checks: Check[] }) {
  // API returns newest first; the chart reads oldest → newest.
  const points = [...checks].reverse()
  if (points.length < 2) {
    return null
  }

  const max = Math.max(...points.map((c) => c.latencyMs), 1)
  const x = (i: number) => PAD + (i * (W - 2 * PAD)) / (points.length - 1)
  const y = (ms: number) => H - PAD - (ms / max) * (H - 2 * PAD)

  return (
    <figure className="chart">
      <figcaption className="muted">
        Latency, last {points.length} checks · max {max} ms
      </figcaption>
      <svg
        viewBox={`0 0 ${W} ${H}`}
        role="img"
        aria-label={`Latency over the last ${points.length} checks, max ${max} ms`}
      >
        <line className="baseline" x1={PAD} x2={W - PAD} y1={H - PAD} y2={H - PAD} />
        <polyline
          className="line"
          points={points.map((c, i) => `${x(i)},${y(c.latencyMs)}`).join(' ')}
        />
        {points.map((c, i) => (
          <g key={c.id}>
            <title>
              {`${new Date(c.checkTimeUtc).toLocaleString()} · ${c.latencyMs} ms${c.success ? '' : ' · failed'}`}
            </title>
            {!c.success && <circle className="fail" cx={x(i)} cy={y(c.latencyMs)} r={4} />}
            <circle className="hit" cx={x(i)} cy={y(c.latencyMs)} r={8} />
          </g>
        ))}
      </svg>
    </figure>
  )
}

export default App
