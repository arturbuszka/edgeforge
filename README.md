# EdgeForge

Edge-native process monitoring: a camera stream turned into events, locally.

```text
Frame -> Vision -> Decision -> Event
```

The first installation is a nine-hen coop — a 1:9 scale factory. A real physical
process, running continuously, that we can instrument freely. See
[docs/product.md](docs/product.md) for why that is the point.

Camera input is real: USB device, RTSP URL or video file. The detector is still
a deterministic placeholder, so the whole pipeline runs without a GPU or a
model, and the interfaces are ready for a real ONNX detector.

## Run

```bash
# video file / USB index ("0") / rtsp:// URL
dotnet run --project src/EdgeForge.Demo -- samples/line.mp4

# synthetic sequence, nothing connected
dotnet run --project src/EdgeForge.Demo -- ""

dotnet test
```

Source precedence: CLI argument > `EDGEFORGE_Camera__Source` > `appsettings.json`.
Keep camera credentials out of source control by using the environment variable.

## Projects

- `EdgeForge.Core` — domain contracts (`IFrameSource`, `IVisionDetector`, `IInspector`, `IInspectionAction`)
- `EdgeForge.Vision` — capture and detection adapters
- `EdgeForge.Runtime` — pipeline orchestration and latency metrics
- `EdgeForge.Device` — output abstraction
- `EdgeForge.Demo` — runnable end-to-end demo
- `EdgeForge.Core.Tests` — unit tests

## Product principle

The model is not the product.

The product is:

`perception -> decision -> event -> measurable record`

Only events leave the device. Never frames.

## Next

1. real ONNX detector, replacing `SampleDetector`
2. count and zone decision, replacing `LabelInspector`
3. per-hen tracking
4. events with duration (entered/left feeder)
5. persisted event history
6. temperature / humidity
7. alerts and anomalies
8. same architecture on one production line

Staged plan in [docs/demo.md](docs/demo.md). Working agreement in
[docs/instruction-agent.md](docs/instruction-agent.md).
