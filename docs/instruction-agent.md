# EdgeForge — Project Context & Engineering Instructions

You are working on EdgeForge.

EdgeForge is NOT a CRUD application, SaaS dashboard, generic AI demo, or portfolio toy.

The long-term goal is to build a small industrial edge product that can eventually be demonstrated and sold to real manufacturing customers.

The first product hypothesis is:

    Camera
      ↓
    Edge computer
      ↓
    Local computer vision inference
      ↓
    Process decision (counts, zones, states)
      ↓
    Event (log / MQTT / GPIO / PLC)
      ↓
    Measurable process record

The first installation is a nine-hen coop.

This is not a poultry product. It is a 1:9 scale factory: a real, continuously
running physical process that we own, can instrument freely, and can break
without consequences.

The initial use case is monitoring a live scene and turning it into events:
- how many hens are visible (9 / 9?)
- entry and exit from the monitored zone
- presence at the feeder
- presence at the drinker
- where time is spent
- unusual inactivity

Explicitly NOT in scope: health or disease detection. Counting, tracking and
zone occupancy are honest computer-vision problems. Diagnosis is not.

The system must work locally without cloud connectivity. Video never leaves the
device; only events do.

--------------------------------------------------
BUSINESS CONTEXT
--------------------------------------------------

The eventual product is intended for small and medium-sized industrial processes where:
- monitoring or visual inspection is currently manual,
- an error or an unnoticed state has a measurable cost,
- a large machine-vision system would be too expensive or unnecessarily complex,
- the customer wants a relatively small, deployable edge appliance.

We are NOT trying to compete with large machine-vision vendors by building a gigantic platform.

The initial value proposition is:

"Observe one concrete process with a local edge device and turn it into events a human or a system can act on."

The coop is how we earn the right to say that. It gives us a demonstrator that
has run for weeks against real light, real motion and real failure modes,
instead of a slide deck.

A successful pilot should prove:
1. detection quality over days, not minutes,
2. end-to-end latency,
3. reliability under realistic conditions,
4. a useful event stream,
5. measurable business value.

The same architecture must carry to a plant unchanged in shape:

    coop     camera -> edge -> chicken_count=8 -> event log
    factory  camera -> edge -> defect_detected -> MES

Do not optimize for UI polish.

Do not build a dashboard unless explicitly requested. A dashboard over a
placeholder detector is a screenshot, not a demonstrator.

--------------------------------------------------
ENGINEERING PRINCIPLES
--------------------------------------------------

The most important architectural principle is:

    PERCEPTION != DECISION != ACTION

A computer vision model produces observations.

The inspection layer decides whether the observations satisfy the process requirements.

The action layer performs a physical or logical response.

Keep these concerns separated.

Example:

    Camera
      ↓
    IVisionDetector
      ↓
    Detection[]
      ↓
    IInspector
      ↓
    InspectionResult
      ↓
    IInspectionAction
      ↓
    GPIO / Relay / PLC / Alarm

The domain/core layer must remain testable without physical hardware.

Hardware-specific code must be isolated behind interfaces.

Avoid leaking camera SDKs, GPIO libraries, PLC libraries, or ML framework types into the core domain.

--------------------------------------------------
CURRENT REPOSITORY
--------------------------------------------------

Current projects:

src/
    EdgeForge.Core
    EdgeForge.Vision
    EdgeForge.Runtime
    EdgeForge.Decision
    EdgeForge.Device
    EdgeForge.Storage
    EdgeForge.Demo
    EdgeForge.Ui
    EdgeForge.Api

tests/
    EdgeForge.Core.Tests
    EdgeForge.Storage.Tests

Current prototype already contains:

EdgeForge.Core
- Frame
- IFrameImage
- BoundingBox
- Detection
- InspectionStatus
- InspectionResult
- IFrameSource
- IVisionDetector
- IInspector
- IInspectionAction

EdgeForge.Vision
- FrameSourceSpec        (USB index / rtsp:// / file, parsed and testable)
- OpenCvFrameSource      (real capture, bounded channel, drop counting)
- MatFrameImage
- OnnxVisionDetector     (YOLOv8 via ONNX Runtime, DirectML or CPU)
- LetterboxResize, BoxProjection, GpuAdapters
- PresenceInspector      (is the target class in frame)
- CountInspector         (count against an expected population; for the coop)
- SampleDetector, LabelInspector  (placeholders, kept for the no-model path)
- DetectorFactory        (builds the detector/inspector pair from configuration)

EdgeForge.Runtime
- InspectionPipeline
- InspectionMetrics      (capture queue / inference / decision / action / total)
- InspectionSession      (the capture loop, latest-wins publishing, buffer pooling)
- FrameSnapshot, FrameWriter
- FrameBroadcaster       (encode once, fan out to many viewers, drop slow ones)
                         UNCALLED since MediaMTX took over video delivery.
                         Preserved on purpose, tests and all - do not delete it,
                         and do not wire it back into the live-view path.

EdgeForge.Decision
- MealDetector           (presence -> meal events; the debounce state machine)
- MealThresholds         (start / end / minimum duration)
- MealEvent, DailyMealLog
- MealJournal            (append-only meals.jsonl)
- StatsFile              (stats.json for the OBS overlay, written atomically)
- MealEventSink          (IInspectionAction decorator wiring the above in)
- MealSinkFactory

EdgeForge.Device
- ConsoleInspectionAction

EdgeForge.Demo
- executable end-to-end prototype, camera or synthetic

EdgeForge.Ui
- Avalonia live viewer: video, detection overlay, statistics

EdgeForge.Storage
- SQLite mirror of meals.jsonl, for the range queries a flat file cannot answer.
  The journal stays the source of truth; this is a rebuildable view over it.

EdgeForge.Api
- local REST surface plus a SignalR push channel, and it hosts the capture loop.
  Serves counts and timestamps only - video comes from MediaMTX, not from here.
  See the exception noted under "Do NOT create a web application" below.

Real camera input is DONE. Frames come from a USB device, an RTSP URL or a video
file, and the demo falls back to a synthetic sequence when no source is set.

The DETECTOR is DONE and verified against a real cat at the bowls: yolov8m
through ONNX Runtime, DirectML on a GTX 1660 Ti, 41 ms per frame (22.9 FPS
headless), reporting `cat 76%` from an overhead angle. Fine-tuning is not needed.
SampleDetector and LabelInspector remain only as the fallback for a machine with
no model exported; they are not on the path to anything.

The DECISION LAYER is DONE for the bowls. PresenceInspector answers the
per-frame question, and EdgeForge.Decision debounces those readings into meal
events: presence for longer than N seconds starts a meal, absence longer than M
seconds ends it. Events are appended to meals.jsonl and summarised in stats.json,
which an OBS Browser Source reads locally for the stream overlay.

--------------------------------------------------
TECHNOLOGY DIRECTION
--------------------------------------------------

Primary language:

C#

Target framework:

.NET 10

Expected future technologies:

- ONNX Runtime for local inference
- YOLO or another suitable object detection model
- OpenCV or equivalent for image/video capture and processing
- USB or GigE industrial cameras
- GPIO / relay interfaces
- OPC UA
- Modbus
- MQTT where appropriate

Do not add all of these now.

Introduce technology only when required by the next milestone.

Prefer boring, reliable engineering over framework experimentation.

--------------------------------------------------
IMPORTANT PRODUCT DIRECTION
--------------------------------------------------

The system is an EDGE product.

Therefore the following properties matter:

- offline operation
- predictable latency
- observability
- hardware abstraction
- deterministic decision logic
- graceful failure
- versioned models
- reproducible configuration
- ability to measure end-to-end latency
- ability to diagnose failures in the field

Do not assume permanent internet connectivity.

Do not make cloud services a hard dependency.

--------------------------------------------------
WHAT IS DONE
--------------------------------------------------

DETECTOR — done. yolov8m through ONNX Runtime, DirectML on a GTX 1660 Ti, 41 ms
per frame, verified against a real cat at the bowls at `cat 76%`. OnnxVisionDetector
loads the model once, letterboxes the frame, runs inference, applies NMS and
returns domain Detection objects. No ONNX Runtime type crosses into the core.

DECISION LAYER (bowls) — done. Two rules, deliberately separate:

- PresenceInspector answers the per-frame question: is the target class in frame.
  It stays raw and undebounced, because that is the honest reading of detector
  quality and it is what the overlay and the event log show.
- EdgeForge.Decision answers the over-time question: MealDetector debounces
  presence into meal events (start after N seconds present, end after M seconds
  absent, discard anything under a minimum duration), DailyMealLog counts today,
  MealJournal appends them to meals.jsonl, and StatsFile writes stats.json.

The debounce is driven by frame timestamps rather than the wall clock, so a
recorded file replays to the same events it produced live, and a pipeline stall
cannot masquerade as the animal leaving.

MealEventSink wires the decision layer in as an IInspectionAction decorator
rather than as an inspector. Two reasons: writing files inside IInspector.Inspect
would fold disk I/O into DecisionLatency, which measures the cost of the rule
itself; and decorating leaves the per-frame reading visible instead of
overloading InspectionResult.Reason with two different questions. This is a
compromise either way — the layering has three levels and this is a fourth
concern — and it was chosen because it is the cheapest one to move later. The
state machine lives in EdgeForge.Decision and knows nothing about where it is
called from.

--------------------------------------------------
WHAT WE ARE BUILDING NEXT
--------------------------------------------------

Meals are counted at the BOWLS, not per cat. Four cats are one presence signal,
so two cats eating side by side is one meal with a peak count of two. That is the
honest limit of what detection without identity can claim.

The next milestone is TRACKING, which is what lifts the meal counter from
bowl-level to cat-level:

- a cat is an identity across frames, not a box in one
- MealDetector takes identities instead of a count
- meals.jsonl gains a per-cat attribution
- unusual inactivity becomes answerable for one animal rather than the scene

After that, in the order the staged plan gives:

- zones (feeder, drinker) as distinct regions rather than one scene
- events with duration per zone
- temperature and humidity
- alerts and anomalies

UNKNOWN still matters most where evidence is missing. A hen behind a beam is not
an absent hen, and a momentary count of 8 is not an escape. The same reasoning
already runs at the bowls: a missed frame at 76% confidence is not a cat leaving,
which is why absence is measured in seconds and not in frames.

Follow the staged plan in docs/demo.md. Do not skip ahead to V4.

The implementation should proceed incrementally.

Do NOT replace the entire architecture.

Do NOT rewrite the project into another framework.

Do NOT create a web application.

One exception, requested explicitly and scoped deliberately: EdgeForge.Api plus a
small React front-end in web/, serving a live view of the bowls for the household.

Video does NOT pass through .NET. MediaMTX pulls the camera's RTSP stream and
republishes it to the browser over WebRTC; the API serves counts and timestamps.
That is what keeps "only events leave the device, never frames" true rather than
carved out - the predecessor of this, EdgeForge.Web, did serve MJPEG and did need
a carve-out.

The front-end is one page: a video element and a statistics HUD. No accounts, no
roles, no authentication framework. It is NOT the product direction and nothing in
the staged plan may depend on it. Do not extend it into a dashboard, and do not
add authentication, accounts or cloud exposure to it.

Do NOT create a database unless a concrete requirement appears. Persisted event
history (V2) was that requirement, and it is now met twice over: meals.jsonl
remains the durable append-only record, and EdgeForge.Storage keeps a SQLite
mirror of it for the range queries a flat file cannot answer.

The journal is the source of truth; the database is a rebuildable view over it,
refilled by an idempotent backfill on every start. Do not add a second database,
and do not put anything in SQLite that is not already in the journal.

Do NOT introduce unnecessary dependency injection/container/configuration infrastructure just for the sake of it.

EdgeForge.Api registers five services and no more, because IHubContext (SignalR's
push-from-outside-a-request handle) and IDbContextFactory cannot be constructed by
hand. Everything else there - InspectionSession, the detector pair, FrameWriter,
MealEventSink - is still plain `new`, exactly as Demo and Ui build theirs.

--------------------------------------------------
NAMING
--------------------------------------------------

IInspector, IInspectionAction, InspectionResult and InspectionPipeline are
inspection-era names carried over from packaging.

In the coop they mean: occupancy/count rules, an event sink, a process
observation, and the loop that connects them.

Keep the names for now. Rename only once the coop behaviour is settled and the
rename can be done in one deliberate pass. Renaming contracts while the domain
underneath them is still moving is churn, not progress.

--------------------------------------------------
PERFORMANCE
--------------------------------------------------

Edge performance matters.

Measure:

- frame capture latency
- inference latency
- decision latency
- action latency
- total end-to-end latency
- FPS
- dropped frames

Do not prematurely optimize.

But make measurements explicit.

Do not claim that a model is "real-time" without measuring it.

--------------------------------------------------
RELIABILITY
--------------------------------------------------

This is an industrial system, not a notebook.

Think about failure modes:

- camera disconnected
- camera produces invalid frames
- model unavailable
- inference timeout
- low confidence
- no object detected
- multiple objects detected
- action device unavailable
- configuration invalid

The system should fail safely.

For an inspection system, UNKNOWN should not silently become OK.

Prefer explicit states such as:

    OK
    NOK
    UNKNOWN
    FAULT

if the domain eventually requires them.

--------------------------------------------------
TESTING
--------------------------------------------------

Tests are important for the decision layer.

Keep tests independent from physical hardware.

At minimum test:

- expected count present -> OK
- fewer than expected -> NOK
- nothing detected -> UNKNOWN, never silently OK
- a hen inside a zone -> zone occupied
- a hen on a zone boundary -> stable, not flapping
- a one-frame count dip -> debounced, not an alert
- low confidence
- overlapping/duplicate detections
- malformed/empty input

Do not write tests that merely test implementation details.

Test process behaviour.

--------------------------------------------------
CODE STYLE
--------------------------------------------------

Prefer:

- small classes
- explicit interfaces at hardware boundaries
- immutable records for domain data
- dependency inversion
- composition over inheritance
- async APIs where I/O is involved
- cancellation support
- clear naming

Avoid:

- giant classes
- static global state
- god services
- generic "Manager" classes
- unnecessary abstractions
- speculative frameworks
- premature microservices
- unnecessary patterns

This application should be able to run as a single edge process.

A modular monolith is perfectly acceptable.

--------------------------------------------------
VERY IMPORTANT: DO NOT OVERENGINEER
--------------------------------------------------

At every step ask:

"Does this help us put a camera in front of a real scene and emit a correct event?"

If not, it probably does not belong in the current milestone.

Do not implement:
- authentication
- users
- roles
- billing
- multi-tenancy
- SaaS
- web dashboards
- Kubernetes
- cloud deployment
- elaborate event buses
- distributed microservices

unless explicitly requested later.

--------------------------------------------------
DEFINITION OF SUCCESS
--------------------------------------------------

The first major success condition is NOT:

"the application has a nice UI."

It is:

A hen moves in the coop,
the edge computer captures the frame,
the local model detects it,
the decision layer resolves count and zone,
and the system emits a correct, timestamped event.

And then it keeps doing that, unattended, for a week.

Eventually we want to demonstrate:

    live scene
       ↓
    camera
       ↓
    local inference
       ↓
    count / zone state
       ↓
    event

while displaying:

    hens seen vs expected
    feeder visits
    drinker visits
    activity
    confidence
    latency
    model version

Only events leave the device. Never frames.

The final demo should fit into approximately 90 seconds.

--------------------------------------------------
HOW YOU SHOULD WORK
--------------------------------------------------

When asked to implement a feature:

1. Inspect the existing architecture first.
2. Reuse existing abstractions where appropriate.
3. Make the smallest coherent change.
4. Keep the project buildable.
5. Add/update tests for domain behaviour.
6. Explain architectural decisions briefly.
7. Do not silently introduce unrelated technologies.
8. Do not rewrite working code without a reason.
9. Prefer an end-to-end vertical slice over building infrastructure without a use case.

When there are multiple reasonable designs, prefer the simplest one that keeps future hardware integration possible.

The project is being built by an engineer with both .NET and mechatronics experience.

Use that advantage.

The goal is to bridge software and the physical world.