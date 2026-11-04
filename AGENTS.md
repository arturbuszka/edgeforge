# EdgeForge: instructions for coding agents

EdgeForge turns a camera stream into process events, locally. It is a small edge
product, not a SaaS or a generic AI demo. The primary installation is a nine-hen
coop, used as a 1:9 scale factory; the `line` scenario applies the same core to
items on a belt. Video never leaves the device, only events do.

Read [docs/architecture.md](docs/architecture.md) before changing structure and
[docs/demo.md](docs/demo.md) for the staged plan. Follow the stages in order.

## Architecture rules

- Perception, decision and action stay separate:
  `IVisionDetector -> IInspector -> IInspectionAction`.
- No camera SDK, GPIO, PLC or ML framework type crosses into `EdgeForge.Core`.
- Over-time decisions run on frame timestamps, never the wall clock, so a recording
  replays to the events it produced live.
- Absence is measured in seconds, not frames. A pipeline stall is not an event.
- `UNKNOWN` never silently becomes `OK`.
- A missing GPU degrades to the CPU. A corrupt model or bad configuration is a real
  failure and must surface.
- A new product on the line is a new profile. A new kind of question is a new case in
  `Scenario` plus a new `IDecisionSink`, never a change to the hosts. Do not add a
  scenario without a real scene for it.

## Storage and video

- Journals (`meals.jsonl`, `items.jsonl`) are the source of truth. SQLite is a
  rebuildable view: put nothing there that is not in the journal.
- Video goes through MediaMTX, never through .NET. `FrameBroadcaster` is preserved
  on purpose: do not delete it, and do not wire it back into the live view.
- `EdgeForge.Api` and `web/` are one local page, not the product direction. Do not
  add accounts, authentication, cloud exposure or dashboards to them.

## Scope

Add technology only when the next milestone needs it. Do not add SaaS features,
Kubernetes, cloud deployment, event buses or microservices unless explicitly asked.
A modular monolith running as one edge process is the target. Keep health and disease
detection out of scope: counting, tracking and zone occupancy are honest
computer-vision problems, diagnosis is not.

## How to work

1. Inspect the existing code first and reuse its abstractions.
2. Make the smallest coherent change and keep the build free of warnings.
3. Test process behaviour without hardware, not implementation details.
4. Measure latency explicitly. Do not call anything real-time without a measurement.
5. Never commit camera URLs: they belong in `.env` and `appsettings.Local.json`.

```bash
dotnet build EdgeForge.sln -c Release
dotnet test EdgeForge.sln -c Release
EDGEFORGE_PROFILE=eggs dotnet run --project src/EdgeForge.Demo -- samples/eggs.mp4
```
