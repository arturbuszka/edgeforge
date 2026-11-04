# Product hypothesis

## What this actually is

EdgeForge is an edge-native pipeline that turns a camera stream into process events.

The first installation is a nine-hen coop. This is not a poultry product. It is a
1:9 scale factory: a real physical process, running continuously, that we do not
have to ask anyone's permission to instrument.

The coop gives us what a slide deck cannot — a system exposed to real light, real
motion and real failure modes before we ever walk into a plant.

## Initial wedge

Local vision on a live scene, converted into counted, timestamped events.

For the coop:

- how many hens are visible (9 / 9?)
- entry and exit from the monitored zone
- presence at the feeder
- presence at the drinker
- where time is spent
- unusual inactivity

Deliberately NOT in scope: health or disease detection. Counting, tracking and
zone occupancy are honest computer-vision problems. Diagnosis is not, and
claiming it would poison the demo.

## Value proposition

A local appliance that observes a process and emits events without a cloud
dependency. Video never leaves the device; only metadata does.

```text
14:32:18  camera_01  chicken_count=8  feeding_zone=2  activity=0.71
```

That line is the product. Not the video stream, not the model.

## Why this transfers

The same sentence sells to a manufacturer:

> observe the object -> convert to events -> store them -> show the operator

A defect on a line and a hen at a feeder are the same problem to this codebase:
something is in a zone, or it is not, and someone needs a record of it.

```text
coop     camera -> edge -> chicken_count=8   -> event log
factory  camera -> edge -> defect_detected   -> MES
```

## Pilot questions

These are the same questions a plant will ask, asked first where the cost of a
wrong answer is nine hens rather than a production line.

1. Is the target state detected reliably enough, over days rather than minutes?
2. Is end-to-end latency compatible with the process?
3. Does the event log survive a week without a human resetting anything?
4. Does the financial impact justify deployment?

The first product is not a generic AI platform. It is a solution to one
measurable process problem, proven on a process we own.
