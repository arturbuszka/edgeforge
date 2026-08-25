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

## Counting meals

Detection alone does not answer "how many times did they eat today". A cat at the
bowls for a minute is ~1400 frames carrying a detection, and counting those gives
1400, not 1. The decision layer in `EdgeForge.Decision` debounces the stream into
events:

| Setting | Default | What it does |
| ------- | ------- | ------------ |
| `Meals.MinPresenceSeconds` | 5 | Present for this long before a meal starts. Rejects a cat walking past. |
| `Meals.MaxAbsenceSeconds` | 60 | Gone for this long before it ends. Cats leave the bowl and come back; a shorter gap is a pause, not a second meal. |
| `Meals.MinMealSeconds` | 10 | Shortest meal worth logging, first sighting to last. |

The defaults come from how a cat actually eats — a visit is tens of seconds long
and contains pauses. `MaxAbsenceSeconds` is the one most worth tuning: too low
and one visit is logged as five, too high and two genuine visits merge into one.
Read `meals.jsonl` after a few days and adjust rather than guessing twice.

Two details that are load-bearing:

- **Absence is measured in seconds, not in empty frames.** At 41 ms per frame a
  dropped-frame stall would otherwise read as the cat leaving, and a pipeline
  hiccup would end a meal that never ended.
- **The counter survives a restart.** Today's meals are rebuilt from
  `meals.jsonl` on startup, so a process that dies at noon does not show zero for
  the rest of the day.

Meals are counted **at the bowls, not per cat**. Four cats are one presence
signal, so two eating side by side is one meal with a peak count of two. Per-cat
attribution needs tracking, which is the next milestone.

Two files are written beside the binary:

- `meals.jsonl` — append-only, one JSON object per completed meal. The persisted
  event history, and what the thresholds get re-tuned against.
- `stats.json` — the current day, rewritten atomically so a reader never sees a
  half-written file.

```json
{
  "updatedUtc": "2026-08-22T18:04:11.0000000+00:00",
  "date": "2026-08-22",
  "mealsToday": 7,
  "lastMealEndedUtc": "2026-08-22T17:58:40.0000000+00:00",
  "currentlyFeeding": false,
  "totalFeedingSecondsToday": 412
}
```

Set `Meals.Enabled` to `false` to leave the pipeline exactly as it was.

### Stream overlay

[overlay/meals.html](overlay/meals.html) is an OBS Browser Source that shows the
counter. Add it with **Local file** ticked, pointed at that file; it polls
`stats.json` from the same folder every two seconds and dims itself if the file
goes stale, so a stopped pipeline does not leave a number on screen that has
quietly stopped being true.

Either copy the page next to the binary, or point `Meals.StatsPath` at the folder
the page lives in.

No HTTP server is involved in the overlay — OBS reads a local file, and that file
carries counts and timestamps, never frames.

The overlay is independent of `EdgeForge.Api` and keeps working unchanged whether
the API runs or not. `stats.json` still carries exactly the six fields documented
above; the richer document the API serves at `/api/stats` adds its extra fields
only there, so nothing about what OBS reads has changed.

## Live view

A browser page showing the camera with a statistics HUD over it: meals today,
whether a cat is at the bowls right now, how long for. Three pieces, and they are
deliberately independent.

**Video** comes from MediaMTX, not from .NET. It pulls the camera's RTSP stream
and republishes it to the browser over WebRTC, which means the inference process
never encodes or serves a pixel — *only events leave the device* holds without an
exception. It also means MediaMTX going down stops the picture and not the
counting, and stopping the pipeline stops the counting and not the picture.

**Data** comes from `EdgeForge.Api`: a REST surface plus a SignalR channel that
pushes a stats snapshot whenever it changes. It also hosts the capture loop, so it
is the daemon as well as the API.

**The page** is a small React app in `web/`.

### Running it

```
cp .env.example .env          # then fill in MTX_CAMERA_URL
docker compose up -d          # MediaMTX

dotnet run --project src/EdgeForge.Api

cd web && npm install && npm run dev     # development, on :5173
```

For production there is no Node involved: `npm run build` writes the bundle into
the API's `wwwroot`, and the API then serves the page from its own origin.

```
cd web && npm run build
dotnet run --project src/EdgeForge.Api   # page at http://127.0.0.1:5080/
```

### MTX_WEBRTC_HOST — read this before debugging a black screen

This is the setting that eats an afternoon. A container behind NAT gathers ICE
candidates from its own interfaces and advertises `172.x.x.x`, which the browser
cannot route to. The WHEP POST returns 201, the SDP answer is valid, MediaMTX logs
a happy reader — and the video never starts. Nothing looks wrong anywhere.

Set `MTX_WEBRTC_HOST` in `.env` to the address the *browser* reaches MediaMTX on:

| Watching from | Value |
| --- | --- |
| this machine | `127.0.0.1` |
| a phone on the LAN | the host's LAN IP, e.g. `192.168.1.50` |
| over Tailscale | the tailnet address from `tailscale ip -4` |

Comma-separate for several: `127.0.0.1,192.168.1.50,100.x.y.z`.

Two related traps: port 8189 must be mapped as `/udp` (it is, in the committed
compose file — do not drop the suffix), and `network_mode: host` is a no-op on
Windows Docker Desktop, so it would work on a Linux box and silently fail here.

**Tailscale Funnel is out of scope.** It would publish the camera to the open
internet. Keep this on the tailnet.

**Run `EdgeForge.Ui` or `EdgeForge.Api`, not both.** Against one camera they open
two RTSP sessions and two ONNX sessions on one GPU, and they keep two independent
meal histories. They are alternatives, not companions.

`EdgeForge.Api` with `Camera:Source` empty still serves stats and starts no
capture loop, which is useful while working on the front-end.

### Meal history

Completed meals go to three places, and the redundancy is deliberate:

- `meals.jsonl` — the durable append-only record, and the source of truth.
- `stats.json` — the current snapshot, which the OBS overlay reads.
- `meals.db` — a SQLite mirror, for the range queries `/api/meals?from=&to=`
  answers. It is a view, not a second record: delete it and restart, and an
  idempotent backfill rebuilds it from the journal.

### Credentials

The camera URL carries the admin password in its userinfo field and lives in
exactly two gitignored files: `.env` for MediaMTX, `appsettings.Local.json` for
the pipeline. Neither `appsettings.json` nor any committed compose file contains
it, and `/api/config` deliberately never returns it — that response goes to every
browser that loads the page.

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
- `EdgeForge.Decision` — presence debounced into meal events, and where they are written
- `EdgeForge.Device` — output abstraction
- `EdgeForge.Demo` — runnable end-to-end demo (headless)
- `EdgeForge.Storage` — SQLite mirror of `meals.jsonl`, for range queries
- `EdgeForge.Ui` — Avalonia live viewer: video, detection overlay, statistics
- `EdgeForge.Api` — REST + SignalR, and the daemon that hosts the capture loop
- `web/` — React page: WebRTC player from MediaMTX with a statistics HUD
- `EdgeForge.Core.Tests`, `EdgeForge.Storage.Tests` — unit tests

## Product principle

The model is not the product.

The product is:

`perception -> decision -> event -> measurable record`

Only events leave the device. Never frames.

## Next

1. ~~real ONNX detector, replacing `SampleDetector`~~ — done
2. ~~decision layer: presence debounced into events with duration~~ — done
3. ~~persisted event history (`meals.jsonl`)~~ — done
4. per-cat tracking, lifting the meal counter from the bowls to the animal
5. zone decision (feeder, drinker) on top of the count rule
6. temperature / humidity
7. alerts and anomalies
8. same architecture on one production line

Staged plan in [docs/demo.md](docs/demo.md). Working agreement in
[docs/instruction-agent.md](docs/instruction-agent.md).
