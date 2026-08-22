# EdgeForge

Edge-native process monitoring: a camera stream turned into events, locally.

```text
Frame -> Vision -> Decision -> Event
```

The first installation is a nine-hen coop — a 1:9 scale factory. A real physical
process, running continuously, that we can instrument freely. See
[docs/product.md](docs/product.md) for why that is the point.

Camera input is real: USB device, RTSP URL or video file. Detection is real too
— a YOLOv8 model running locally through ONNX Runtime. With no model configured
the pipeline falls back to a deterministic placeholder, so it still runs on a
machine with no weights on disk.

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

### Live viewer

A desktop window over the same pipeline, for aiming the camera and watching what
the detector actually sees:

```bash
dotnet run --project src/EdgeForge.Ui
```

Type a source into the window and press Start. It draws detection boxes with
confidence, and reports how many animals are in frame alongside FPS, per-stage
latency and dropped frames — the numbers that say whether the pipeline is
keeping up with the camera.

`Capture.AutoSaveDetections` writes frames containing detections to `captures/`,
throttled by `MinIntervalSeconds` and `MinConfidence` and capped by `MaxFiles`.
That is how the training set for telling individual animals apart gets built.

## Model

The weights are **not** in this repo — `models/*.onnx` is ignored. Export
YOLOv8n once, locally:

```bash
pip install ultralytics
yolo export model=yolov8n.pt format=onnx imgsz=640 opset=12
mv yolov8n.onnx models/
```

Then point `Detector.ModelPath` at it in
[src/EdgeForge.Demo/appsettings.json](src/EdgeForge.Demo/appsettings.json), or
via `EDGEFORGE_Detector__ModelPath`. An empty `ModelPath` keeps the placeholder
detector.

**Important:** `Detector.NetSize` must match the exported `imgsz` (640 here). A
wrong size gives empty or shifted detections. The `dets` column in the per-frame
log is the raw detection count — if it is 0, the problem is detection, not the
count rule.

`Detector.ClassId` is a COCO class index. 15 = cat, used during development
because cats are indoors and always available; 14 = bird for the coop. Both run
the identical code path, so retargeting is that number plus `ClassName` and
nothing else. `Detector.ExpectedCount` is the population the count rule expects.

## Projects

- `EdgeForge.Core` — domain contracts (`IFrameSource`, `IVisionDetector`, `IInspector`, `IInspectionAction`)
- `EdgeForge.Vision` — capture and detection adapters (OpenCV, ONNX Runtime)
- `EdgeForge.Runtime` — pipeline orchestration and latency metrics
- `EdgeForge.Device` — output abstraction
- `EdgeForge.Demo` — runnable end-to-end demo (headless)
- `EdgeForge.Ui` — Avalonia live viewer: video, detection overlay, statistics
- `EdgeForge.Core.Tests` — unit tests

## Product principle

The model is not the product.

The product is:

`perception -> decision -> event -> measurable record`

Only events leave the device. Never frames.

## Next

1. ~~real ONNX detector, replacing `SampleDetector`~~ — done
2. zone decision on top of the count rule
3. per-hen tracking
4. events with duration (entered/left feeder)
5. persisted event history
6. temperature / humidity
7. alerts and anomalies
8. same architecture on one production line

Staged plan in [docs/demo.md](docs/demo.md). Working agreement in
[docs/instruction-agent.md](docs/instruction-agent.md).
