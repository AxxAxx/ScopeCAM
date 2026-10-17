# ScopeCAM

A minimal Windows viewer for AmScope (ToupTek) microscope cameras: live feed,
exposure and gain sliders, auto-exposure toggle, and PNG snapshots. It
auto-connects to the first camera it finds and reconnects automatically if
the camera is unplugged.

## Running

Double-click `ScopeCAM.exe` (there is a **ScopeCAM shortcut on the Desktop**),
or run the built exe at:

```
src\bin\Release\net8.0-windows\ScopeCAM.exe
```

Close the AmScope application first — only one program can use the camera at
a time.

## Controls

- **Auto exposure** — on by default. The camera manages exposure/gain; the
  **Target** slider biases how bright auto exposure aims (lower it if shiny
  solder blows out). Uncheck for manual control via the Time/Gain sliders.
- **Time** — logarithmic slider over the camera's full exposure range
  (shown in ms/µs). Manual mode only.
- **Gain** — analog gain in percent (100 % = 1×). Manual mode only.
- **Auto white balance** — one-push color correction (also runs automatically
  on connect). Re-run it after changing lighting.
- **Sharpen** — SDK sharpening strength 0–500 (default 150).
- **Flicker** — anti-banding for AC lighting: 50 Hz (default), 60 Hz, or DC.
- **Flip horizontal/vertical** — match image movement to hand movement.
- **Crosshair** — center reference overlay.
- **Freeze** — pause the live feed to inspect; Resume to continue.
- **Snapshot** — saves a PNG to `Pictures\ScopeCAM\`.

## Startup tuning (30+ fps, stable auto exposure)

On connect the app configures the camera for crisp, high-framerate live view:

- USB frame speed level set to the camera's maximum (full USB 3.0 bandwidth)
  and realtime mode on (stale buffered frames are dropped, lowest latency).
- Auto exposure capped at 33 ms (the SDK default allows 350 ms ≈ 3 fps);
  gain capped at 500 % so it can't get too noisy. Verified ~36 fps on the
  MU503 at 1280×960.
- Auto-exposure anti-hunting: full-frame average metering (the SDK default
  meters only the brightest 1 % of pixels, which chases specular solder
  reflections), raised reaction threshold, and damped exposure/gain
  transitions — fixes the back-and-forth brightness flicker.
- One-push auto white balance, 50 Hz anti-flicker, moderate sharpening.

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
