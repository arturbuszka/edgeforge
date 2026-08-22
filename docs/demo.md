# Demo plan

The demo is not a slideshow about edge AI. It is a machine that has been running
in a coop for weeks, shown live.

## 90 seconds

1. Show the live camera view of the coop.
2. Show the detection overlay — hens boxed and counted.
3. Show `9 / 9`.
4. A hen enters the feeding zone.
5. Show the event emitted: `feeding_zone=1`.
6. Show the event log filling with timestamped lines.
7. Show today's counters: hens, feeder visits, drinker visits, activity.
8. Show latency and FPS.
9. End with `LOCAL EDGE INFERENCE / NO CLOUD`.

## The line that closes it

> This is my own demonstrator. A camera on the edge watches a process, turns the
> image into events, stores them and shows them. I want to do exactly this on one
> operation in your plant.

## Roadmap

| Stage | Content | Status |
| ----- | ------- | ------ |
| V0 | camera + recording + detect hens | detector still a placeholder |
| V1 | tracking per hen, counting, zones | |
| V2 | event history, temperature/humidity | |
| V3 | feeding / drinking / egg events | |
| V4 | alerts, anomaly detection | |
| V5 | same architecture on one production line | |

## Hardware

Deliberately cheap. Nothing here needs industrial hardware to prove the
architecture.

```text
IP or USB camera -> mini PC / Raspberry Pi / Jetson -> model -> tracker
  -> MQTT / API -> storage -> dashboard
```

Plus two or three sensors when V2 arrives: temperature, humidity, light.

## Order of work

Do not build the dashboard, SaaS, authentication or fleet management before the
detector is real and the event loop runs unattended for a week.

A dashboard over a placeholder detector is a screenshot, not a demonstrator.
