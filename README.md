# ScopeCAM

A minimal Windows viewer for AmScope (ToupTek) microscope cameras: live feed,
exposure and gain sliders, auto-exposure toggle, and PNG snapshots. It
auto-connects to the first camera it finds and reconnects automatically if
the camera is unplugged.

## Running

Double-click **`ScopeCAM.exe` in this folder** (or the ScopeCAM shortcut on
the Desktop — it points to the same file).

Close the AmScope application first — only one program can use the camera at
a time.

After code changes, regenerate the exe with:

```
dotnet publish src\ScopeCAM.csproj -c Release -p:PublishSingleFile=true -p:DebugType=none --no-self-contained -o .
```

## Controls

All sliders also take typed values in the box next to them (Enter or click
away to apply; both `12.5` and `12,5` work).

- **Size** — camera resolution. Full 2560×1922 runs ~14 fps (the USB 3.0
  throughput limit); binned 1280×960 runs ~36 fps. **Frame rate** is shown
  live below the size.
- **Auto exposure** — on by default. **Target** biases how bright auto
  exposure aims (lower it if shiny solder blows out). **Max ms** is the
  longest exposure auto mode may use — it sets the frame rate floor
  (fps ≈ 1000 / Max ms, up to the sensor readout limit); auto exposure
  compensates with gain. Uncheck for manual control via Time/Gain.
- **Time** — logarithmic slider over the camera's full exposure range (ms).
  Manual mode only.
- **Gain** — analog gain in percent (100 % = 1×). Manual mode only.
- **Auto white balance** — one-push color correction (runs automatically on
  first connect); the result lands in the **Temp**/**Tint** sliders, which
  you can then adjust manually.
- **Gamma / Contrast / Saturate** — color correction; **Reset colors**
  returns all color settings to camera defaults.
- **Sharpen** — SDK sharpening strength 0–500 (default 150; applied with a
  noise threshold so flat areas aren't speckled).
- **Denoise** — 0–100 (default 0), spatial noise reduction done in the app
  (the camera's own denoise is a no-op on this model — verified — so it's
  computed here). Blends each pixel toward its neighbours, so high values
  soften fine detail; pair with Sharpen.
- **Auto sharpen** — analyzes the current frame's noise and sets Sharpen and
  Denoise automatically (more noise → less sharpening, more denoise). A
  heuristic starting point; nudge from there.
- **Demosaic** — the raw-to-color conversion algorithm. EA (default) is
  fast; try AHD if fine detail shows rainbow speckle — it suppresses false
  color best but costs more CPU (may reduce fps at full resolution).
- **Flicker** — anti-banding: DC (default), 50 Hz, or 60 Hz. Leave on DC
  unless you see rolling bands in the image. Note: in the AC modes the
  camera firmware locks exposure to coarse steps (10 ms at 50 Hz) and its
  auto exposure oscillates between steps — if you need an AC mode, prefer
  manual exposure.
- **Flip horizontal/vertical** — match image movement to hand movement.
- **Crosshair** — center reference overlay.
- **Focus peaking** — highlights in-focus (high-contrast) edges in bright
  green. Turn the microscope's focus knob until the most of your subject
  lights up — that's peak focus. **Peak sens** controls how aggressively
  edges are flagged.
- **Freeze** — pause the live feed to inspect; Resume to continue.
- **Snapshot** — saves a PNG to `Pictures\ScopeCAM\`.

**Zoom:** scroll the mouse wheel over the video to zoom in/out (centered on
the cursor), drag with the left button to pan, and double-click to reset.
This is a display zoom — it doesn't change what Snapshot saves.
- **Save defaults** — stores the current resolution, exposure mode and
  values, sharpening, flicker, flips, and crosshair to
  `%APPDATA%\ScopeCAM\settings.json`; they are re-applied every time the
  app connects.

## Startup tuning (30+ fps, stable auto exposure)

On connect the app configures the camera for crisp, high-framerate live view:

- USB frame speed level set to the camera's maximum (full USB 3.0 bandwidth)
  and realtime mode on (stale buffered frames are dropped), frame queues
  shortened to the minimum, and the USB grab thread priority raised —
  minimum capture-to-display latency.
- The live view renders through a custom fast-scaling control instead of
  PictureBox (whose high-quality bicubic scaling costs tens of ms per 5 MP
  frame on the UI thread).
- Auto exposure capped at the Max ms goal time (default 33 ms; the SDK
  default allows 350 ms ≈ 3 fps); gain capped at 500 % so it can't get too
  noisy. Verified ~36 fps on the MU503 at 1280×960.
- Auto-exposure anti-hunting: full-frame average metering (the SDK default
  meters only the brightest 1 % of pixels, which chases specular solder
  reflections), raised reaction threshold, and damped exposure/gain
  transitions.
- Anti-flicker defaults to DC. This matters: in 50/60 Hz AC mode the
  firmware quantizes exposure to the mains half-period and its auto
  exposure oscillates between the two steps bracketing the ideal exposure
  (measured ~6 Hz brightness pumping on the MU503, under every
  AUTOEXP_POLICY setting). With DC mode auto exposure converges in under
  a second and stays put.
- One-push auto white balance, moderate sharpening.
- EA (Edge Aware) demosaic instead of the SDK default bilinear, which
  produced rainbow false color along fine traces and pin edges (verified by
  A/B captures). EA removes the fringing at the lowest CPU cost of the
  SDK's algorithms.

## How it works

The app talks directly to `amcam.dll`, the ToupTek SDK that ships with the
AmScope software (loaded from `C:\Program Files\AmScope\AmScope\x64\`; the
DLL is not copied or redistributed). `src/AmcamNative.cs` holds the P/Invoke
declarations; `src/MainForm.cs` is the whole UI. Frames are streamed in pull
mode: the SDK signals an image event on its own thread, and the UI thread
pulls the newest 24-bit frame straight into a GDI+ bitmap (frame events are
coalesced, so the UI never backlogs).

## Building

Requires the .NET 8 SDK (x64):

```
dotnet build src\ScopeCAM.csproj -c Release
```

## Design doc

See `docs/superpowers/specs/2026-10-17-scopecam-viewer-design.md`.
