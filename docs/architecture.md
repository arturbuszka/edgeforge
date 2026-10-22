# Architecture

```text
Camera (USB / RTSP / video file)
  |
  v
IFrameSource          IAsyncEnumerable<Frame>
  |
  v
IVisionDetector
  |
  v
Detection[]
  |
  v
IInspector            presence / count rules
  |
  v
InspectionResult
  |
  v
IInspectionAction
  |
  v
MealEventSink         debounce over time -> meal events
  |
  v
console / event log / meals.jsonl / stats.json / SQLite / SignalR
```

Video takes a separate path and never enters this one: MediaMTX pulls the same
camera and republishes it to the browser. See *Video path* below.

## Scenarios

The layers above are fixed. What fills three of them depends on the scene, and
`EdgeForge.Runtime.Scenario` is where that choice is made, from configuration:

| | bowls | line |
| --- | --- | --- |
| `IVisionDetector` | YOLO (COCO class), or blob | blob (geometry), or YOLO (custom classes) |
| `IInspector` | `PresenceInspector` | `ItemRuleInspector` — per frame, not the verdict |
| decision sink | `MealEventSink` → `MealDetector` | `LineEventSink` → `LineCounter` → `ItemTracker` + `ItemRule` |
| event | meal: start, end, duration | verdict: item, status, reason, median measurements |

Hosts supply their innermost action (console, viewer log) and talk to the decision
layer only through `IDecisionSink`: `Flush` at end of stream, `Readout` for
figures. Anything scenario-specific beyond that — the meal API and its SQLite
mirror — is reached through the concrete type by the one host that needs it.

`Detection.Measurements` is how a detector reports physical quantities (`width_mm`
…), and `LineOptions.Limits` names them, so a new measured product is a profile
rather than a code change. `InspectionResult.Timestamp` carries the frame's scene
time — capture time for a camera, the file's own clock for a recording — and both
decision layers run on it, so a replay yields the events the footage contains.

## The four levels

The pipeline is the same four levels we later sell to a plant. Each is a
milestone, not a component to build all at once.

| Level | Claim | Layer |
| ----- | ----- | ----- |
| 1 Vision | I can see what physically happens | `IFrameSource` |
| 2 Edge AI | I turn the image into events | `IVisionDetector` |
| 3 MES | Events become process data | `IInspector` + `IInspectionAction` |
| 4 AI | History reveals anomalies | not built yet |

We are at level 3. A YOLOv8 model runs locally through ONNX Runtime behind
`IVisionDetector` — on the GPU via DirectML, falling back to the CPU when no
DX12 device is available — `PresenceInspector` turns its detections into a
present-or-not reading, and `EdgeForge.Decision` turns a stream of those readings
into meal events with a start, an end and a duration. Those events are appended
to `meals.jsonl` and summarised in `stats.json`.

`PresenceInspector` itself stays undebounced on purpose. The raw per-frame
reading is what tells us the truth about detector quality, and it is what the
overlay and the event log show; smoothing it there would hide exactly what we
want to see. Debouncing happens one layer down, over those readings.

Presence rather than count, because the two scenes ask different questions. At
the bowls the normal state is an empty frame, so `CountInspector`'s
expected-versus-observed rule reports the ordinary as the exception. A coop with
a known flock size is the opposite case, and `CountInspector` stays for it.

## The decision layer

Counting meals needs the transition between absent and present, held over time.
That does NOT require tracking: an earlier version of this document said
debouncing would arrive with per-animal identity, and that was wrong. Debounce
over a presence signal is useful on its own — it is what turns 1400 frames
carrying a detection into seven events. Tracking upgrades the same counter from
bowl-level to cat-level later; it is not a precondition for having one.

`MealDetector` is a three-state machine — idle, starting, feeding — driven by
frame timestamps rather than the wall clock. Two consequences follow, and both
are the reason for that choice:

- a recorded file replays to exactly the events it produced live, so a threshold
  can be re-tuned against yesterday's footage
- absence is measured in seconds, not in consecutive empty frames, so a dropped
  frame or a stalled pipeline cannot masquerade as the animal walking away

It is pure: no clock, no I/O, no locks. Persisting events and writing the overlay
file belong to `MealEventSink` downstream, which keeps a simulated hour of
footage a matter of microseconds in a test.

### Where it is wired in

`MealEventSink` implements `IInspectionAction` and decorates the existing one.
Putting the state machine in a stateful `IInspector` instead would work, and the
choice is a genuine trade-off rather than an obvious win:

- an inspector writing `stats.json` folds disk I/O into `DecisionLatency`, a
  metric that currently measures the cost of the rule alone
- an inspector would have to carry both the per-frame reading and the meal state
  through one `InspectionResult.Reason` string — two questions, one field
- against that, a sink is arguably the action layer doing decision work

Neither placement matches the three-level split exactly, because this is a fourth
concern. The sink was chosen as the cheaper one to move: all the logic lives in
`EdgeForge.Decision` and is independent of where it is called from.

## Principles

- perception is separate from decision
- decision is separate from the response
- core is testable without hardware
- measure the complete loop, not only model inference
- the camera is a stream, not a request
- only events leave the device, never frames. The live view is not an exception
  to this. MediaMTX pulls the camera's RTSP stream directly and republishes it to
  the browser over WebRTC, so the inference process never encodes or serves a
  pixel; `EdgeForge.Api` serves counts and timestamps. The video path and the
  event path are separate all the way down, which is why the rule holds for the
  .NET side without qualification.

## Naming

`IInspector` and `IInspectionAction` are inspection-era names. In the coop they
carry occupancy rules and an event sink respectively. The names stay until the
detector is real — renaming contracts before the behaviour is settled is churn.

## Video path

Video does not enter the .NET process for display. MediaMTX runs in Docker, pulls
`rtsp://…` from the camera on demand and republishes it as WebRTC for the browser,
with HLS as a fallback. The pipeline opens its own RTSP session against the same
camera for inference.

Two readers, one camera, no coupling: MediaMTX going down stops the picture and
not the counting, and stopping the pipeline stops the counting and not the
picture. `sourceOnDemand` means MediaMTX holds no session at all while nobody is
watching, so the camera serves two clients only when someone actually is.

The browser gets the stream over WHEP — one HTTP POST carrying an SDP offer, one
201 carrying the answer. The failure mode worth knowing about is ICE: a container
behind NAT advertises an address the browser cannot route to, signalling succeeds,
and the video stays black with nothing in the logs to say why. `MTX_WEBRTC_HOST`
in `.env` is the fix; the README has the three cases.

### What this replaced

`FrameBroadcaster` encoded JPEGs on a dedicated thread and fanned them out to
MJPEG viewers. It worked, and the three rules it enforced were each worth getting
right — encode once off the capture path, drop for slow consumers rather than
block, never let a `Mat` cross into an async handler. But it put a 15-30 ms encode
next to a ~41 ms inference budget and made the .NET process responsible for
delivering video.

It is still in `EdgeForge.Runtime`, uncalled and still covered by its tests, kept
against the day .NET itself needs to record a clip. Nothing on the live-view path
constructs it. The HLS future this section once reserved for "a third subscriber
piping `EncodedFrame` to ffmpeg" is served better by MediaMTX, which does it
natively.

## Storage

`meals.jsonl` is the source of truth: append-only, one JSON object per line,
survivable by a text editor. `EdgeForge.Storage` keeps a SQLite mirror of it,
because a flat file cannot answer "meals between these two dates" without reading
all of it.

The database is a view, not a second record. It is rebuilt from the journal by a
backfill that runs on every start — idempotent through a unique index on the
meal's start instant, so it needs no marker file. Delete the `.db` and restart,
and it comes back. Nothing goes into SQLite that is not already in the journal.

Both derive their notion of "today" from `MealJournal.LocalDayOf`, deliberately
the same function. Two implementations of "which local day did this meal start
on" disagree across midnight and across a DST boundary, and the symptom is the
OBS overlay and the web HUD reporting different counts with neither looking wrong.

## Where the session lives

`InspectionSession`, `FrameSnapshot` and `FrameWriter` sit in `EdgeForge.Runtime`
beside `InspectionPipeline`, not in the viewer that first needed them, so the
headless API host reuses the capture loop rather than duplicating its buffer
pooling and frame-lifetime discipline. That makes `EdgeForge.Runtime` reference
`EdgeForge.Vision`. If a Vision-free Runtime is ever needed, splitting these three
files into an `EdgeForge.Session` project is a file move, not a redesign.

## Frame ownership

`IFrameSource` yields frames; the consumer owns each one and disposes
`Frame.Image`. Frames dropped inside the source are disposed by the source. One
owner at every point, because native image buffers do not forgive two.

## Latency

`InspectionMetrics` records capture queue delay, inference, decision, action and
total. Capture latency is the age of the frame when inspection starts — it is
what reveals a backed-up channel, and the number that tells us whether a claim
of "real-time" is true.
