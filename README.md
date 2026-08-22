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
once, locally. `yolov8m` is the default choice — see *Where inference runs*
below for why the larger model is close to free on the GPU:

```bash
pip install ultralytics
yolo export model=yolov8m.pt format=onnx imgsz=640 opset=12
mv yolov8m.onnx models/
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
nothing else.

At the bowls the active rule is `PresenceInspector`: it reports whether the
target class is in frame and treats an empty frame as normal, because for most
of the day it is. `CountInspector` — which marks any count other than
`Detector.ExpectedCount` as a fault — remains for a scene with a known, fixed
population such as the coop; pointing it at the bowls fills the event log with
the animals' own absence. `ExpectedCount` is read only by that rule, so it is
currently unused.

## Where inference runs

`Detector.ExecutionProvider` is `directml` (GPU) or `cpu`. DirectML runs on any
DX12 GPU with the stock driver — no CUDA Toolkit, no cuDNN. If no such device
can be opened the detector falls back to the CPU on its own, so a machine
without a GPU still runs; the startup banner names whichever provider actually
opened, not the one that was asked for.

`Detector.GpuDeviceId` picks the GPU by adapter index, and on a machine with
switchable graphics it matters more than anything else in this file. Index 0 is
not the fastest adapter — it is whichever one the driver enumerates first, which
on a laptop is usually the integrated chip. Measured here, yolov8m, mean
inference per frame:

| Where | Latency | FPS |
| ----- | ------- | --- |
| CPU (Ryzen 7 3750H) | 952 ms | 1.1 |
| DirectML, adapter 0 — Radeon Vega 10 | 215 ms | 4.6 |
| DirectML, adapter 1 — GTX 1660 Ti | 41 ms | 22.9 |

There is no reliable way to pick that index automatically: driver enumeration
order and DirectML order are not the same — here the registry lists the GTX
first while DirectML answers to it as index 1 — so a heuristic would choose
confidently and wrongly. The banner lists every GPU it can see; if inference is
slow, try the other index.

The useful part of the GPU numbers is not the speedup. It is that GPU cost is
roughly flat across model sizes — yolov8m costs about what yolov8n does — so
accuracy stops trading against latency and the largest model is the sensible
default. On the CPU that same choice costs about 5x.

### The live viewer competes with the model for the GPU

Running both the model and the window on the discrete card makes the preview
freeze while the pipeline keeps reporting healthy throughput: inference occupies
the GPU almost continuously, and the window renderer never gets scheduled. The
statistics climb, `dets` updates, and the picture sits still — which looks like a
rendering bug and is not one.

The headless demo does not have this problem (no window, no contention), so
`EdgeForge.Demo` is the honest place to measure detection throughput.

For a usable live preview, give the two jobs different GPUs. In Windows:
**Settings → System → Display → Graphics**, add `EdgeForge.Ui.exe`, set it to
**Power saving** (the integrated GPU), then set `Detector.GpuDeviceId` to the
discrete card. The window renders on the integrated chip, the model gets the
discrete one, and neither waits on the other. Drawing one image and a few
rectangles is well within what integrated graphics handle.

Without that split, `GpuDeviceId: 0` (integrated) gives a smooth preview at
~4.6 FPS — which is entirely adequate for counting meals, since a cat at a bowl
stays there for tens of seconds.

### Build details that are load-bearing

The executables set `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` with
`SelfContained=false`, and `EdgeForge.Vision` copies `DirectML.dll` out of the
`Microsoft.AI.DirectML` package explicitly. That package ships the DLL under
`bin/x64-win/` rather than the `runtimes/win-x64/native/` layout the SDK copies
automatically, and its own build targets do not flow past the project that
references it directly. Without the copy, ONNX Runtime binds to the
`DirectML.dll` in `System32` — on Windows 11 a 2022 build, far older than ORT
expects — and session creation fails inside `AbiCustomRegistry` with
`0x80070057`. The failure is silent in effect: the detector falls back to the
CPU and merely runs slow. Check the banner if the numbers look wrong.

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
