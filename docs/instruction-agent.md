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
    EdgeForge.Device
    EdgeForge.Demo

tests/
    EdgeForge.Core.Tests

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
- SampleDetector
- LabelInspector

EdgeForge.Runtime
- InspectionPipeline
- InspectionMetrics      (capture queue / inference / decision / action / total)

EdgeForge.Device
- ConsoleInspectionAction

EdgeForge.Demo
- executable end-to-end prototype, camera or synthetic

Real camera input is DONE. Frames come from a USB device, an RTSP URL or a video
file, and the demo falls back to a synthetic sequence when no source is set.

The current DETECTOR is deliberately fake/deterministic.

SampleDetector returns a fixed label class per frame, and BOX-003 simulates a
wrong label. LabelInspector still encodes label rules.

These two are the leftovers from the packaging-inspection era and they are the
next thing to replace. The architecture around them is sound; the domain inside
them is not yet the coop.

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
WHAT WE ARE BUILDING NEXT
--------------------------------------------------

The next milestone is A REAL DETECTOR.

Camera input already works. What runs on top of it is still a placeholder.

Goal:

    coop camera
       ↓
    real local inference
       ↓
    hens detected + counted
       ↓
    zone / count decision
       ↓
    event

Replace SampleDetector with a real local inference implementation behind the
existing IVisionDetector contract.

Conceptually:

    OnnxVisionDetector : IVisionDetector

Responsibilities:
- load model once, not per frame
- preprocess frame
- run inference
- postprocess predictions
- return domain Detection objects

Do not return ONNX Runtime types from the domain. The ONNX implementation lives
outside the core, in the adapter layer, exactly as OpenCvFrameSource does.

A person-or-animal class from a general pretrained model is an acceptable first
step. Do not start by collecting and labelling a custom dataset — first prove
the pipeline carries real detections end to end.

--------------------------------------------------
MILESTONE AFTER THE DETECTOR
--------------------------------------------------

Then the decision layer becomes the coop's, not the label's.

LabelInspector is replaced by an inspector that answers process questions:

- how many hens are visible, against an expected count
- which zones are occupied (feeder, drinker)
- how long a state has persisted

This is where UNKNOWN matters most. A hen behind a beam is not an absent hen,
and a momentary count of 8 is not an escape. Debouncing a state over time is a
decision-layer concern, not a detector concern.

Then, and only then:

- tracking, so a hen is an identity and not a box
- events with duration (entered feeder / left feeder)
- persisted event history
- temperature and humidity
- alerts and anomalies

Follow the staged plan in docs/demo.md. Do not skip ahead to V4.

The implementation should proceed incrementally.

Do NOT replace the entire architecture.

Do NOT rewrite the project into another framework.

Do NOT create a web application.

Do NOT create a database unless a concrete requirement appears. Persisted event
history (V2) is the point at which one becomes a concrete requirement — a flat
append-only file is an acceptable first answer.

Do NOT introduce unnecessary dependency injection/container/configuration infrastructure just for the sake of it.

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