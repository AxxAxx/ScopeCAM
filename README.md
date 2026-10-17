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

- **Auto exposure** — when checked, the camera manages exposure/gain and the
  sliders follow along (disabled). Uncheck it to take manual control.
- **Exposure** — logarithmic slider over the camera's full exposure range
  (shown in ms/µs).
- **Gain** — analog gain in percent (100 % = 1×).
- **Snapshot** — saves a PNG to `Pictures\ScopeCAM\`.

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
