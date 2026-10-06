import { useEffect, useState } from 'react'
import './App.css'
import { getAccessToken } from './auth.ts'

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

async function getJson<T>(url: string): Promise<T> {
  const token = await getAccessToken()
  const response = await fetch(url, { headers: { Authorization: `Bearer ${token}` } })
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

function App() {
  const [data, setData] = useState<EndpointWithChecks[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loadedAt, setLoadedAt] = useState(0)

  useEffect(() => {
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
          if (!cancelled) setError(e.message)
        })

    refresh()
    const timer = setInterval(refresh, REFRESH_MS)
    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [])

  return (
    <main className="page">
      <h1>API Reliability Dashboard</h1>
      {error && <p className="error">Failed to load: {error}</p>}
      {!data && !error && <p className="muted">Loading…</p>}
      {data?.length === 0 && <p className="muted">No endpoints yet.</p>}
      {data?.map((endpoint) => (
        <section key={endpoint.id} className="card">
          <p className="url">{endpoint.url}</p>
          <Status latest={endpoint.checks[0]} now={loadedAt} />
          <LatencyChart checks={endpoint.checks} />
        </section>
      ))}
    </main>
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
