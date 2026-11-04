import { useEffect, useRef, useState } from 'react'
import { playWhep, stopWhep } from './whep'

/**
 * The video element and its WHEP session.
 *
 * Falls back to HLS when WebRTC cannot connect. That happens on Safari without
 * the right codecs, and - far more often - when MediaMTX is advertising ICE
 * candidates the browser cannot route to, which is what MTX_WEBRTC_HOST exists
 * to fix. The fallback keeps a picture on screen either way; the console message
 * says which path is in use, because a silent fallback would hide a
 * misconfiguration behind five seconds of latency.
 */
export function Player({ config }) {
  const video = useRef(null)
  const [mode, setMode] = useState('connecting')

  useEffect(() => {
    if (!config) return

    const element = video.current
    let session = null
    let cancelled = false

    async function connect() {
      try {
        session = await playWhep(config.streamUrl, element)
        if (cancelled) return

        setMode('webrtc')
      } catch (error) {
        if (cancelled) return

        console.warn('WHEP failed, falling back to HLS:', error)
        setMode('hls')

        // Native HLS only, which covers Safari and iOS. Everything else lands
        // here just to show a clear message rather than a black rectangle, since
        // pulling in hls.js to paper over a WebRTC misconfiguration would hide
        // the actual problem.
        if (element.canPlayType('application/vnd.apple.mpegurl')) {
          element.src = config.hlsUrl
        } else {
          setMode('failed')
        }
      }
    }

    connect()

    return () => {
      cancelled = true
      stopWhep(session, config.streamUrl)
    }
  }, [config])

  return (
    <div className="stage">
      {/* muted is mandatory: without it Chrome blocks autoplay and the video
          silently never starts, with nothing in the console to say why. */}
      <video ref={video} autoPlay muted playsInline />

      {mode === 'connecting' && <p className="notice">Connecting to the stream…</p>}
      {mode === 'failed' && (
        <p className="notice">
          No video. Check that MediaMTX is running and that MTX_WEBRTC_HOST is the
          address this browser reaches it on.
        </p>
      )}
    </div>
  )
}
