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

We are at level 1 with a placeholder for level 2.

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
