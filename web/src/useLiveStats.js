import { useEffect, useState } from 'react'
import { HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr'

/**
 * How long without an update before the HUD calls itself stale. The API pushes
 * on change and the pipeline rewrites its stats every few seconds, so a minute
 * of silence means something stopped - the same threshold and the same reasoning
 * as overlay/meals.html, which dims for exactly this reason: a number that has
 * quietly stopped being true is worse than no number.
 */
const STALE_AFTER_MS = 60_000

/**
 * Live stats over SignalR.
 *
 * Seeds from /api/stats so the HUD is populated before the first push, then lets
 * the hub drive it. The hub also pushes once on connect, so the seed only covers
 * the gap before the socket is up.
 *
 * @returns {{stats: object|null, connected: boolean, stale: boolean}}
 */
export function useLiveStats() {
  const [stats, setStats] = useState(null)
  const [connected, setConnected] = useState(false)
  const [receivedAt, setReceivedAt] = useState(0)
  const [now, setNow] = useState(() => Date.now())

  useEffect(() => {
    let cancelled = false

    // The seed. Failure is fine and deliberately quiet: the hub is about to
    // deliver the same document, and an error here would flash on every reload
    // where the socket simply won the race.
    fetch('/api/stats', { cache: 'no-store' })
      .then((response) => (response.ok ? response.json() : null))
      .then((seed) => {
        if (!cancelled && seed) {
          setStats(seed)
          setReceivedAt(Date.now())
        }
      })
      .catch(() => {})

    const connection = new HubConnectionBuilder()
      .withUrl('/hub/live', {
        // WebSockets only. The fallbacks exist for networks that block them, and
        // this runs on a LAN or a tailnet where they do not - letting it silently
        // degrade to long polling would hide a real problem behind a working HUD.
        transport: HttpTransportType.WebSockets,
        skipNegotiation: true,
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    connection.on('StatsUpdated', (pushed) => {
      if (cancelled) return

      setStats(pushed)
      setReceivedAt(Date.now())
    })

    connection.onreconnected(() => !cancelled && setConnected(true))
    connection.onreconnecting(() => !cancelled && setConnected(false))
    connection.onclose(() => !cancelled && setConnected(false))

    connection
      .start()
      .then(() => !cancelled && setConnected(true))
      .catch(() => !cancelled && setConnected(false))

    return () => {
      cancelled = true
      connection.stop().catch(() => {})
    }
  }, [])

  // A local clock, so "feeding for 4:12" advances between server pushes and the
  // staleness check re-evaluates without one. The server pushes on change; the
  // seconds are the client's job.
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(timer)
  }, [])

  const stale = stats === null || now - receivedAt > STALE_AFTER_MS

  return { stats, connected, stale, now }
}
