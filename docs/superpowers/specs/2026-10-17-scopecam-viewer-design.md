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

Recording video, measurement overlays, multiple cameras.

## Amendment 2026-10-17 (3): goal time, fps display, color controls, latency

- "Max ms" slider/box (auto mode): user-settable cap for auto-exposure time
  (put_MaxAutoExpoTimeAGain) — the "goal time" that sets the frame rate
  floor. Default 33 ms, persisted in defaults.
- Live frame-rate label in the Camera section (mirrors the status bar).
- Manual white balance: Temp [2000,15000] / Tint [200,2500] sliders via
  put_TempTint; AwbOnce result lands in the sliders via EVENT_TEMPTINT.
  Saved defaults store temp/tint; startup AwbOnce only runs when no saved
  white balance exists (so saved WB isn't overwritten).
- Color correction: Gamma [20,180], Contrast [-255,255], Saturation
  [0,255] sliders + Reset colors button.
- Latency: OPTION_FRAME_DEQUE_LENGTH=2, OPTION_BACKEND_DEQUE_LENGTH=2,
  OPTION_THREAD_PRIORITY=2 on top of put_RealTime(1). Display switched
  from PictureBox (high-quality bicubic scaling, slow for 5 MP frames) to
  a custom double-buffered control with fast bilinear scaling (VideoView).
- Physics note: full-res 2560×1922 tops out ~14 fps = USB 3.0 throughput
  (5 MP × 3 B × 14 ≈ 210 MB/s); no software setting can raise that.

## Amendment 2026-10-17 (2): resolution, save defaults, numeric input,
## false-color fix

- Resolution selector (get_ResolutionNumber/get_Resolution/put_eSize).
  Switch is done in place (Stop → put_eSize → restart stream); falls back
  to a full reconnect if that fails. Full 2560×1922 ≈ 14 fps (USB 3.0
  throughput limit), 1280×960 ≈ 36 fps.
- "Save defaults" persists resolution/exposure/sharpen/flicker/flips/
  crosshair to %APPDATA%\ScopeCAM\settings.json, applied on every connect.
- Every slider got a numeric entry box (Enter or focus-leave commits;
  accepts both . and , decimals).
- Color artifact (user-reported rainbow fringing on traces): root cause is
  the SDK's default bilinear demosaic, amplified by sharpening. Verified by
  A/B frame captures (bilinear/VNG/AHD/EA × sharpening 0/150). Fix:
  OPTION_DEMOSAIC = 4 (EA, Edge Aware) — equally clean as AHD and cheapest
  on CPU (AHD measured ~95 % of a core at 1280×960/36 fps, EA ~66 %).

## Amendment 2026-10-17: high framerate, auto color, PCB image quality

Requested by Axel after first version: start in auto exposure at ~30 fps,
use full USB 3.0 bandwidth, automatic color control, and image-quality
features used by other microscope viewers (researched from the ToupTek SDK
header and vendor viewer feature sets).

Startup configuration (`ApplyStartupDefaults`):
- `put_Speed(get_MaxSpeed())` — max USB frame-speed level (not guaranteed
  to be the default), plus `put_RealTime(1)` for lowest display latency.
- Auto exposure on with `put_MaxAutoExpoTimeAGain(33333 µs, ≤500 %)` — the
  SDK default allows 350 ms exposures (≈3 fps); the cap keeps ≥30 fps.
  Measured 36 fps on the MU503 at 1280×960.
- AE anti-hunting (fixes visible exposure flicker on reflective PCBs):
  `OPTION_AUTOEXPOSURE_PERCENT=100` (full-average metering instead of the
  default brightest-1 % metering), `OPTION_AUTOEXP_THRESHOLD=10`,
  `OPTION_AUTOEXP_EXPOTIME_DAMP=300`, `OPTION_AUTOEXP_GAIN_DAMP=300`.
- `AwbOnce` after stream start (automatic color), `OPTION_SHARPENING`
  radius 2 / strength 150.
- Anti-flicker defaults to DC (`put_HZ(2)`), NOT 50 Hz. Root cause found
  by logging every AE adjustment + sampled frame brightness: in AC mode
  the firmware quantizes exposure to 10 ms steps and its AE oscillates
  ~6x/s between the two quanta bracketing the ideal exposure (30 ms@100 %
  ↔ 20 ms@133 %; brightness swung 117–170). AUTOEXP_POLICY=3 (Gain
  Preferred) made it worse (gain saturated at 300 %, 10↔20 ms toggling).
  In DC mode exposure is continuous: AE converged in 0.7 s and made zero
  adjustments over the next 35 s (brightness 187.1–188.4). White balance
  was ruled out (temp/tint constant throughout).

New UI: AE target slider (enabled in auto mode), one-push white balance
button, sharpening slider, flicker selector (50/60 Hz/DC), flip H/V,
crosshair overlay, freeze/resume. Controls moved to a right-side panel.

All option constants verified against toupcam.h v60 (indi-3rdparty vendored
copy) and probed for support against the installed amcam.dll v59.
