# ScopeCAM — AmScope Camera Viewer — Design

Date: 2026-10-17
Status: Approved (approach and feature set confirmed by Axel via interactive questions)

## Goal

A simple standalone Windows app that shows the live feed from the connected
AmScope microscope camera, with sliders for exposure and gain, an
auto-exposure toggle, and a snapshot button. It must auto-connect to the
camera on startup.

## Environment facts (verified)

- AmScope software installed at `C:\Program Files\AmScope\AmScope\`.
- The camera SDK is `amcam.dll` (x64) in that folder — ToupTek's "amcam" C API.
  Verified exports include `Amcam_EnumV2`, `Amcam_Open`,
  `Amcam_StartPullModeWithCallback`, `Amcam_PullImageV2`,
  `Amcam_get/put_ExpoTime`, `Amcam_get/put_ExpoAGain`,
  `Amcam_get_ExpTimeRange`, `Amcam_get_ExpoAGainRange`,
  `Amcam_get/put_AutoExpoEnable`.
- Camera is connected: USB device VID_0547&PID_3511 ("USB3.0 Camera").
- No .NET SDK was installed; .NET 8 SDK installed via winget as part of this work.

## Approach (chosen)

C# WinForms app on .NET 8 (`net8.0-windows`, x64), calling `amcam.dll` via
P/Invoke. Produces a standalone `ScopeCAM.exe`. The DLL is loaded from the
AmScope install folder via a `DllImportResolver` (fallback: a copy next to
the exe), so we don't redistribute the DLL.

Alternatives considered: Python + tkinter (simplest, but user prefers a
double-click exe); Python + PySide6 (wheel availability risk on Python 3.14).

## Components

1. **`AmcamNative.cs`** — minimal P/Invoke layer over amcam.dll:
   open/close, enumeration (device display name), pull-mode streaming with
   event callback, image pull, exposure/gain get/put + ranges,
   auto-exposure get/put. Keeps a GC-rooted reference to the callback
   delegate. Structs: `AmcamDeviceV2` (displayname[64], id[64], model ptr),
   `AmcamFrameInfoV2` (width, height, flag, seq, timestamp).

2. **`MainForm.cs`** — the UI:
   - PictureBox (fill, zoom) showing the live feed.
   - Bottom panel: `[x] Auto exposure` checkbox, Exposure slider + ms label,
     Gain slider + % label, `Snapshot` button.
   - Status strip: camera model name, live FPS.

## Behavior

- **Auto-connect:** on startup, enumerate cameras and open the first one.
  If none found, show "No camera found" and retry every 2 s until one appears.
- **Streaming:** pull mode with callback. On `AMCAM_EVENT_IMAGE`, pull the
  24-bit RGB frame into a reusable buffer (SDK thread), then marshal to the
  UI thread (coalesced — skip frames if the UI is behind) and blit into a
  24bpp `Bitmap` shown in the PictureBox.
- **Exposure slider:** range from `get_ExpTimeRange` (microseconds); label
  shows milliseconds. Moving the slider calls `put_ExpoTime`.
- **Gain slider:** range from `get_ExpoAGainRange` (percent, 100 = 1×).
- **Auto exposure:** checkbox calls `put_AutoExpoEnable`; sliders are
  disabled while auto is on. On `AMCAM_EVENT_EXPOSURE`/`AMCAM_EVENT_EXPO_GAIN`
  the sliders follow the camera's chosen values (without re-sending them).
  Initial checkbox state read from the camera (defaults to auto on).
- **Snapshot:** saves the most recent frame as PNG to
  `Pictures\ScopeCAM\scopecam_<yyyyMMdd_HHmmss>.png`; shows the path in the
  status strip.
- **Disconnect:** on `AMCAM_EVENT_ERROR`/`AMCAM_EVENT_DISCONNECTED`, close
  the camera, show "Camera disconnected — waiting…", and resume the retry
  loop.

## Error handling

- All SDK calls return HRESULT; failures on non-critical calls (e.g. a put
  while disconnecting) are ignored; failures during open/start show a
  message and fall back to the retry loop.
- The event callback never throws across the native boundary (top-level
  try/catch).

## Testing

Hardware-bound GUI app; no unit test suite. Verification is: build cleanly,
run with the camera attached, confirm feed + both sliders + auto toggle +
snapshot work. (Build verified by Claude; runtime verification by Axel.)

## Out of scope (YAGNI)

Resolution switching, white balance, recording video, measurement overlays,
multiple cameras.
