# EdgeForge

[![CI](https://github.com/arturbuszka/edgeforge/actions/workflows/ci.yml/badge.svg)](https://github.com/arturbuszka/edgeforge/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Edge-native process monitoring in C#/.NET 10: a camera stream turned into events,
locally. Only events leave the device, never frames.

```text
Camera -> Detector -> Per-frame rule -> Decision over time -> Event -> Journal
```

A detector says what is in one frame, and it is noisy. EdgeForge turns those
readings into a small number of events you can act on and audit. A minute of
presence is about 1400 detected frames and exactly one event.

The primary installation is a nine-hen coop, used as a 1:9 scale factory: a real
physical process, running continuously, that can be instrumented freely. See
[docs/product.md](docs/product.md) for why that is the point.

## Try it in a minute

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). No camera, GPU or
model required: this runs the `line` scenario on the synthetic belt in `samples/`.

```bash
EDGEFORGE_PROFILE=eggs dotnet run --project src/EdgeForge.Demo -- samples/eggs.mp4
dotnet test
```

```text
Items judged  : 30
OK / NOK / ?  : 23 / 7 / 0
Reject rate   : 23.3 %
Items / min   : 72
NOK Undersize : 7
```

## Scenarios

The pipeline, metrics and journals stay the same. Configuration picks the scenario.

| Scenario | Question | Detector | Event | Verified on |
| --- | --- | --- | --- | --- |
| `bowls` (default) | is an animal present, and for how long | YOLOv8 through ONNX Runtime | a visit, with start, end and duration | live camera |
| `line` | is this item in spec | geometry, or a custom YOLO model | one verdict per item, with median measurements | synthetic video with known sizes |

`bowls` is the presence-over-time scenario: its name and its `meals.jsonl` journal
come from the first scene it watched, and its target class is configuration.
On the synthetic belt all 30 verdicts are correct, with width within 0.3 mm of the
drawn size. It has not yet run on a real belt.

## Engineering highlights

- **Replayable decisions.** State machines run on frame timestamps, not the wall
  clock, so a recording replays to the same events and thresholds can be re-tuned
  against yesterday's footage.
- **Noise is not an event.** Presence is debounced in seconds rather than frames,
  so a dropped-frame stall cannot read as a subject leaving.
- **Graceful degradation.** A missing GPU falls back to the CPU and the startup
  banner names the provider that actually opened. A corrupt model fails loudly.
- **Backpressure you can see.** Capture uses a bounded channel, and dropped frames
  are counted rather than hidden.
- **Measured latency.** Queue, inference, decision and action are timed per frame.
- **Durable, rebuildable state.** An append-only journal is the source of truth. The
  SQLite mirror can be deleted and rebuilt by an idempotent backfill.
- **Unknown is a state.** Missing evidence never silently becomes `OK`.

Mean inference per frame, yolov8m, measured on one laptop:

| Where | Latency | FPS |
| --- | --- | --- |
| CPU (Ryzen 7 3750H) | 952 ms | 1.1 |
| DirectML, Radeon Vega 10 | 215 ms | 4.6 |
| DirectML, GTX 1660 Ti | 41 ms | 22.9 |

## Projects

| Project | Role |
| --- | --- |
| `EdgeForge.Core` | Domain contracts, free of camera and ML types |
| `EdgeForge.Vision` | Capture and detection adapters (OpenCV, ONNX Runtime, geometry) |
| `EdgeForge.Runtime` | Pipeline, scenario assembly and latency metrics |
| `EdgeForge.Decision` | State machines turning readings into events, and the journals |
| `EdgeForge.Storage` | SQLite mirror of the journal for range queries (EF Core) |
| `EdgeForge.Device` | Output abstraction |
| `EdgeForge.Demo` | Headless end-to-end host |
| `EdgeForge.Ui` | Avalonia viewer: video, detections and statistics |
| `EdgeForge.Api` | REST and SignalR, and the daemon hosting the capture loop |
| `web/` | React page: WebRTC video from MediaMTX with a statistics HUD |

## Documentation

- [Operations guide](docs/operations.md): models, profiles, live view, GPU selection
- [Architecture](docs/architecture.md): layers, decision layer, video path, storage
- [Product hypothesis](docs/product.md) and [staged plan](docs/demo.md)
- [AGENTS.md](AGENTS.md): working agreement for AI coding agents, used in development

## Status

Done: real capture (USB, RTSP, file), local YOLO detection, presence debounced into
events, persisted history, the live view and the `line` scenario.

Next, in order:

1. Flock counting in the coop: `CountInspector` exists and is tested, not yet wired
   into a scenario.
2. Tracking per animal.
3. Feeder and drinker zones.
4. Temperature and humidity.
5. Alerts and anomalies.
6. A defect model and a reject output for the line.

Deliberately out of scope: health or disease detection.

## License

[MIT](LICENSE). Model weights are not in this repo and have their own terms: YOLOv8
weights from Ultralytics are AGPL-3.0 unless you hold an Ultralytics enterprise
license, so check that before any commercial deployment.
