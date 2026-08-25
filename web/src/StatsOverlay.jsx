/**
 * The HUD: counts and timestamps over the video, in the shape a stream overlay
 * takes. Stats only - no boxes, no event feed, no charts.
 *
 * It shares its palette with overlay/meals.html deliberately, so the OBS overlay
 * and this page read as one product rather than two things that happen to count
 * the same cats.
 */

/** mm:ss, or h:mm:ss past an hour. */
function duration(seconds) {
  if (!Number.isFinite(seconds) || seconds < 0) return '–'

  const whole = Math.floor(seconds)
  const s = String(whole % 60).padStart(2, '0')
  const m = Math.floor(whole / 60) % 60
  const h = Math.floor(whole / 3600)

  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${s}` : `${m}:${s}`
}

export function StatsOverlay({ stats, connected, stale, now }) {
  const mealsToday = stats?.mealsToday ?? 0
  const feeding = stats?.currentlyFeeding === true

  // Ticked from the local clock rather than from a server field, so the seconds
  // advance smoothly between pushes instead of jumping once a second.
  const feedingFor =
    feeding && stats?.currentMealStartedUtc
      ? (now - Date.parse(stats.currentMealStartedUtc)) / 1000
      : null

  const cats = stats?.catsOnScreen
  const pipelineRunning = stats?.pipelineRunning === true

  return (
    <div className={`hud${stale ? ' stale' : ''}`}>
      <div className="hud-corner top-left">
        <div className="card hero">
          <span className="count">{mealsToday}</span>
          <span className="label">posiłki dziś</span>
        </div>

        <div className={`card pill${feeding ? ' feeding' : ''}`}>
          <span className="dot" />
          <span>{feeding ? 'je teraz' : 'przy miskach pusto'}</span>
          {feedingFor !== null && <span className="mono">{duration(feedingFor)}</span>}
        </div>
      </div>

      <div className="hud-corner top-right">
        <div className="card stat">
          <span className="label">łącznie dziś</span>
          <span className="mono">{duration(stats?.totalFeedingSecondsToday ?? 0)}</span>
        </div>

        <div className="card stat">
          <span className="label">na obrazie</span>
          {/* null and 0 are different facts: "no cats" versus "nothing is
              looking". Showing a confident 0 for a stopped pipeline would be a
              lie the HUD tells convincingly. */}
          <span className="mono">{cats ?? '–'}</span>
        </div>
      </div>

      <div className="hud-corner bottom-left">
        <div className="card status">
          <span className={`dot${connected && !stale ? ' ok' : ''}`} />
          <span>
            {!connected ? 'brak połączenia z API' : stale ? 'dane nieaktualne' : 'na żywo'}
          </span>
          {connected && !pipelineRunning && <span className="warn">pipeline zatrzymany</span>}
        </div>
      </div>
    </div>
  )
}
