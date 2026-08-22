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
IInspector            zone + count rules
  |
  v
InspectionResult
  |
  v
IInspectionAction
  |
  v
console / event log / MQTT / GPIO
```

## The four levels

The pipeline is the same four levels we later sell to a plant. Each is a
milestone, not a component to build all at once.

| Level | Claim | Layer |
| ----- | ----- | ----- |
| 1 Vision | I can see what physically happens | `IFrameSource` |
| 2 Edge AI | I turn the image into events | `IVisionDetector` |
| 3 MES | Events become process data | `IInspector` + `IInspectionAction` |
| 4 AI | History reveals anomalies | not built yet |

We are at level 2: a YOLOv8 model runs locally through ONNX Runtime behind
`IVisionDetector` — on the GPU via DirectML, falling back to the CPU when no
DX12 device is available — and `PresenceInspector` turns its detections into a
present-or-not reading. Deliberately without debouncing — that is a
decision-layer concern that arrives with tracking, and until then we want the
raw truth about detector quality.

Presence rather than count, because the two scenes ask different questions. At
the bowls the normal state is an empty frame, so `CountInspector`'s
expected-versus-observed rule reports the ordinary as the exception. A coop with
a known flock size is the opposite case, and `CountInspector` stays for it.
Counting meals needs the transition between absent and present, which belongs
with tracking in the decision layer.

## Principles

- perception is separate from decision
- decision is separate from the response
- core is testable without hardware
- measure the complete loop, not only model inference
- the camera is a stream, not a request
- only events leave the device, never frames

## Naming

`IInspector` and `IInspectionAction` are inspection-era names. In the coop they
carry occupancy rules and an event sink respectively. The names stay until the
detector is real — renaming contracts before the behaviour is settled is churn.

## Frame ownership

`IFrameSource` yields frames; the consumer owns each one and disposes
`Frame.Image`. Frames dropped inside the source are disposed by the source. One
owner at every point, because native image buffers do not forgive two.

## Latency

`InspectionMetrics` records capture queue delay, inference, decision, action and
total. Capture latency is the age of the frame when inspection starts — it is
what reveals a backed-up channel, and the number that tells us whether a claim
of "real-time" is true.
