import { useEffect, useState } from 'react'
import { Player } from './Player'
import { StatsOverlay } from './StatsOverlay'
import { useLiveStats } from './useLiveStats'

export function App() {
  const [config, setConfig] = useState(null)
  const [configError, setConfigError] = useState(null)
  const { stats, connected, stale, now } = useLiveStats()

  // Fetched once at runtime rather than baked in at build time, so the same
  // bundle works from this machine, from a phone on the LAN and over a tailnet.
  // The API is the one place that knows where MediaMTX is.
  useEffect(() => {
    fetch('/api/config')
      .then((response) => {
        if (!response.ok) throw new Error(response.status)
        return response.json()
      })
      .then(setConfig)
      .catch((error) => setConfigError(String(error)))
  }, [])

  return (
    <main>
      {configError ? (
        <p className="notice">
          Could not load configuration from the API ({configError}). Is EdgeForge.Api running?
        </p>
      ) : (
        <Player config={config} />
      )}

      <StatsOverlay stats={stats} connected={connected} stale={stale} now={now} />
    </main>
  )
}
