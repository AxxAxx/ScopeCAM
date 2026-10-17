# ScopeCAM

A Windows viewer for AmScope (ToupTek) microscope cameras, built for PCB
inspection and soldering. Live feed with a sectioned control panel (Camera,
Exposure, Color, Image, View, Capture), full exposure / gain / white-balance /
color control, focus peaking, mouse-wheel zoom, and PNG snapshots. It
auto-connects to the first camera it finds and reconnects automatically if the
camera is unplugged.

The UI is a light, flat theme (Segoe UI, sectioned groups, accent-blue primary
buttons) and renders video through a custom fast-scaling control so it stays
responsive at full resolution.

## Requirements

- Windows (x64), tested on Windows 11.
- The **.NET 8 Desktop Runtime** (the published exe is framework-dependent).
- The **AmScope software** installed — the app loads its camera SDK
  (`amcam.dll`) from `C:\Program Files\AmScope\AmScope\x64\`. The DLL is not
  copied or redistributed. If you uninstall AmScope, copy that DLL next to
  `ScopeCAM.exe`.
- An AmScope / ToupTek USB camera (developed against the MU503, 2560×1922).

## Running

Double-click **`ScopeCAM.exe`** in the repo root (or the ScopeCAM shortcut on
the Desktop — it points to the same file).

Only one program can use the camera at a time, so close the AmScope
application first.

## Building

Requires the .NET 8 SDK (x64):

```
dotnet build src\ScopeCAM.csproj -c Release
```

To regenerate the single-file `ScopeCAM.exe` in the repo root:

```
dotnet publish src\ScopeCAM.csproj -c Release -p:PublishSingleFile=true -p:DebugType=none --no-self-contained -o .
```

## Controls

Every slider also takes a typed value in the box beside it (press Enter or
click away to apply; both `12.5` and `12,5` are accepted).

**Camera**
- **Size** — camera resolution. Full 2560×1922 runs ~14 fps; binned
  1280×960 runs ~36 fps. The full-res ceiling is the sensor's readout
  speed, not USB bandwidth or data size (verified: 8-bit RAW, one-third the
  bytes of RGB24, gives the same 14 fps), so lower resolution is the only
  way to a faster feed. **Frame rate** is shown live below it.

**Exposure**
- **Auto exposure** — on by default. **Target** biases how bright auto
  exposure aims (lower it if shiny solder blows out). **Max ms** is the
  longest exposure auto mode may use — it sets the frame-rate floor
  (fps ≈ 1000 / Max ms, up to the sensor readout limit); auto exposure makes
  up the difference with gain. Uncheck for manual control.
- **Time** — logarithmic exposure slider over the camera's full range (ms).
  Manual mode only.
- **Gain** — analog gain in percent (100 % = 1×). Manual mode only.

**Color**
- **Auto white balance** — one-push correction (also runs on first connect);
  the result lands in the **Temp** / **Tint** sliders for manual tweaking.
- **Gamma / Contrast / Saturate** — color adjustment. **Reset colors**
  returns all color settings to camera defaults.

**Image**
- **Sharpen** — SDK sharpening 0–500 (default 150), applied with a noise
  threshold so flat areas aren't speckled.
- **Denoise** — 0–100 (default 0), spatial noise reduction done in the app
  (the camera's own denoise is a no-op on this model — verified). Blends each
  pixel toward its neighbours, so high values soften detail; pair with Sharpen.
- **Auto sharpen** — estimates the current frame's noise and sets Sharpen and
  Denoise automatically (noisier → less sharpen, more denoise). A heuristic
  starting point.
- **Demosaic** — raw-to-color algorithm. EA (default) is fast; AHD suppresses
  false color best but costs more CPU.
- **Flicker** — anti-banding: DC (default), 50 Hz, or 60 Hz. Leave on DC
  unless you see rolling bands. In AC modes the firmware locks exposure to
  coarse steps and auto exposure oscillates — prefer manual exposure there.

**View**
- **Flip horizontal / vertical** — match on-screen movement to hand movement.
- **Crosshair** — center reference overlay.
- **Focus peaking** — highlights in-focus (high-contrast) edges in bright
  green; turn the scope's focus knob until the most of the subject lights up.
  **Peak sens** controls how aggressively edges are flagged.

**Capture**
- **Freeze** — pause the live feed to inspect; press again to resume.
- **Snapshot** — saves a PNG to `Pictures\ScopeCAM\`.
- **Save defaults** — stores resolution, exposure mode and values, white
  balance, color, sharpen/denoise, demosaic, flicker, flips, crosshair, and
  focus-peaking to `%APPDATA%\ScopeCAM\settings.json`, re-applied on every
  connect.

**Zoom** — scroll the mouse wheel over the video to zoom (centered on the
cursor), drag with the left button to pan, and double-click to reset. This is
a display zoom only; it does not change what Snapshot saves.

## Image quality for PCB work

On connect the app configures the camera for a crisp, low-latency, high-frame-
rate live view. The notable choices (all found by measurement against the
MU503):

- **Low latency** — maximum frame-speed level, realtime mode (stale frames
  dropped), minimum frame-queue depth, raised USB grab thread priority.
  (At full resolution the frame rate is capped by sensor readout, ~14 fps,
  not by these settings; they minimize the delay, and raise the rate at
  lower resolutions.)
- **30+ fps auto exposure** — exposure capped at the Max ms goal time (default
  33 ms; the SDK default of 350 ms would cap the feed at ≈3 fps). ~36 fps at
  1280×960.
- **Stable auto exposure** — full-frame average metering (the SDK default
  meters only the brightest 1 %, which chases specular glints), a raised
  reaction threshold, and damped transitions.
- **Anti-flicker defaults to DC** — in 50/60 Hz AC mode the firmware quantizes
  exposure to the mains half-period and auto exposure pumps between steps
  (~6 Hz brightness oscillation). DC gives continuous exposure that converges
  and holds.
- **EA demosaic** instead of the SDK default bilinear, which produced rainbow
  false color along fine traces and pin edges (verified by A/B captures).

### Reducing ring-light reflections

Specular glare (washed-out white reflections from a ring light) is an optical
problem, not a software one — software can't recover blown-out highlights. The
effective fixes, in order of impact: a **cross-polarizer** (polarizing film on
the light + a crossed analyzer on the lens — the pro standard), a **diffuse
dome** illuminator, **low-angle/oblique** lighting, or simply **tilting** the
board so the reflection misses the lens.

## Project layout

```
src/
  Program.cs       — entry point
  MainForm.cs      — UI, camera lifecycle, and the app-side image pipeline
  VideoView.cs     — fast video surface with wheel-zoom / pan / crosshair / peaking
  AmcamNative.cs   — P/Invoke declarations over amcam.dll
  AppSettings.cs   — saved defaults (%APPDATA%\ScopeCAM\settings.json)
  ScopeCAM.csproj
docs/superpowers/specs/  — design doc and amendments
ScopeCAM.exe       — published single-file launcher (gitignored)
```

Frames are streamed in pull mode: the SDK raises an image event on its own
thread, and the UI thread pulls the newest 24-bit frame (events are coalesced,
so the UI never backlogs). Denoise and focus peaking run in that pull path.

## Design doc

See `docs/superpowers/specs/2026-10-17-scopecam-viewer-design.md`.
