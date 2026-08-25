/**
 * WHEP (WebRTC-HTTP Egress Protocol): one HTTP POST carrying an SDP offer, one
 * 201 carrying the answer. That is the entire protocol for a receive-only
 * stream - no signalling server, no socket, no library.
 *
 * Written inline rather than pulled from npm on purpose. The packages that wrap
 * this are single-maintainer and longer than the code they wrap, and the failure
 * mode below (ICE candidates the browser cannot route to) is far easier to debug
 * in code you can read.
 *
 * @param {string} url WHEP endpoint, from /api/config.
 * @param {HTMLVideoElement} video Element to attach the incoming track to.
 * @returns {Promise<{pc: RTCPeerConnection, resource: string|null}>}
 */
export async function playWhep(url, video) {
  // No STUN. This is a LAN or a tailnet, and a public STUN server would put an
  // outbound internet dependency on a box that has to keep working offline.
  const pc = new RTCPeerConnection({ iceServers: [] })

  // Receive-only, declared up front. Without these transceivers the offer
  // carries no media sections at all and MediaMTX answers with an empty SDP.
  pc.addTransceiver('video', { direction: 'recvonly' })
  pc.addTransceiver('audio', { direction: 'recvonly' })

  pc.ontrack = (event) => {
    video.srcObject = event.streams[0]
  }

  const offer = await pc.createOffer()
  await pc.setLocalDescription(offer)

  // Sent immediately, without waiting for ICE gathering to complete. MediaMTX
  // accepts a non-trickle offer, and waiting for gathering adds seconds of black
  // screen for no benefit.
  const response = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/sdp' },
    body: pc.localDescription.sdp,
  })

  if (!response.ok) {
    pc.close()
    throw new Error(`WHEP ${response.status} ${response.statusText}`)
  }

  const answer = await response.text()
  await pc.setRemoteDescription({ type: 'answer', sdp: answer })

  // The Location header is the session resource, for DELETE on teardown.
  return { pc, resource: response.headers.get('Location') }
}

/**
 * Tears a WHEP session down. MediaMTX reaps abandoned sessions on its own, but
 * telling it explicitly is what makes sourceOnDemandCloseAfter fire promptly -
 * otherwise the camera keeps serving a stream nobody is watching.
 *
 * @param {{pc: RTCPeerConnection, resource: string|null}} session
 * @param {string} url The WHEP endpoint, to resolve a relative Location against.
 */
export function stopWhep(session, url) {
  if (!session) return

  const { pc, resource } = session

  try {
    pc.close()
  } catch {
    // Already closed; nothing to do.
  }

  if (!resource) return

  // Best effort: the page is usually unloading by now, and a failed cleanup is
  // not worth an error the user can do anything about.
  try {
    fetch(new URL(resource, url), { method: 'DELETE', keepalive: true }).catch(() => {})
  } catch {
    // An unparseable Location is not worth failing teardown over.
  }
}
