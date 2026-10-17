using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;

namespace ScopeCAM;

public class MainForm : Form
{
    private const int SliderSteps = 1000;        // exposure slider resolution (log scale)
    private const ushort MaxAutoGainCap = 500;   // don't let auto exposure go noisier than 5x
    private const int DefaultSharpness = 150;    // moderate sharpening (range 0–500)

    private readonly VideoView _view = new();
    private readonly ComboBox _resCombo = new();
    private readonly Label _fpsLabel = new();
    private readonly CheckBox _autoExpo = new();
    private readonly TrackBar _aeTargetSlider = new();
    private readonly TextBox _aeTargetBox = new();
    private readonly TrackBar _maxAutoSlider = new();
    private readonly TextBox _maxAutoBox = new();
    private readonly TrackBar _expoSlider = new();
    private readonly TextBox _expoBox = new();
    private readonly TrackBar _gainSlider = new();
    private readonly TextBox _gainBox = new();
    private readonly Button _wbButton = new();
    private readonly TrackBar _wbTempSlider = new();
    private readonly TextBox _wbTempBox = new();
    private readonly TrackBar _wbTintSlider = new();
    private readonly TextBox _wbTintBox = new();
    private readonly TrackBar _gammaSlider = new();
    private readonly TextBox _gammaBox = new();
    private readonly TrackBar _contrastSlider = new();
    private readonly TextBox _contrastBox = new();
    private readonly TrackBar _satSlider = new();
    private readonly TextBox _satBox = new();
    private readonly Button _resetColorButton = new();
    private readonly TrackBar _sharpSlider = new();
    private readonly TextBox _sharpBox = new();
    private readonly ComboBox _flickerCombo = new();
    private readonly CheckBox _flipH = new();
    private readonly CheckBox _flipV = new();
    private readonly CheckBox _crosshair = new();
    private readonly Button _freezeButton = new();
    private readonly Button _snapshotButton = new();
    private readonly Button _saveDefaultsButton = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusCamera = new();
    private readonly ToolStripStatusLabel _statusFps = new();
    private readonly System.Windows.Forms.Timer _reconnectTimer = new();

    private IntPtr _cam = IntPtr.Zero;
    private AmcamNative.EventCallback? _callback; // GC-rooted for the native side
    private Bitmap? _frame;
    private int _framePending; // coalesce image events so the UI thread never backlogs
    private bool _frozen;
    private string _deviceName = "";
    private AppSettings? _settings = AppSettings.Load();
    private int _pendingResolution = -1; // resolution to apply on next connect
    private uint _maxAutoUs = 33_333;    // auto-exposure time cap ("goal time")

    private uint _expoMinUs = 1, _expoMaxUs = 1;
    private ushort _gainMin = 100, _gainMax = 100;
    private bool _updatingUi; // true while controls are being set from camera values

    private readonly Stopwatch _fpsClock = Stopwatch.StartNew();
    private int _fpsFrames;

    public MainForm()
    {
        Text = "ScopeCAM";
        ClientSize = new Size(1280, 860);
        MinimumSize = new Size(800, 600);

        _view.Dock = DockStyle.Fill;

        var controls = BuildControlPanel();

        _statusCamera.Text = "Searching for camera…";
        _statusFps.Alignment = ToolStripItemAlignment.Right;
        _status.Items.Add(_statusCamera);
        _status.Items.Add(new ToolStripStatusLabel { Spring = true });
        _status.Items.Add(_statusFps);

        Controls.Add(_view);
        Controls.Add(controls);
        Controls.Add(_status);

        _reconnectTimer.Interval = 2000;
        _reconnectTimer.Tick += (_, _) => TryConnect();

        Load += (_, _) => TryConnect();
        FormClosing += (_, _) => CloseCamera();
    }

    private Control BuildControlPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 310,
            AutoScroll = true,
            Padding = new Padding(8),
            ColumnCount = 3,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));     // names
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // sliders
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64)); // numeric boxes

        void AddFullRow(Control c)
        {
            int row = panel.RowCount++;
            panel.Controls.Add(c, 0, row);
            panel.SetColumnSpan(c, 3);
        }

        void AddLabeledRow(string name, Control middle, Control? right = null)
        {
            int row = panel.RowCount++;
            panel.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            middle.Dock = DockStyle.Fill;
            panel.Controls.Add(middle, 1, row);
            if (right is not null)
            {
                right.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                panel.Controls.Add(right, 2, row);
            }
            else
            {
                panel.SetColumnSpan(middle, 2);
            }
        }

        // Standard slider row: int slider + numeric box, committed either way.
        void AddIntSlider(string name, TrackBar slider, TextBox box, int min, int max, Action<int> apply)
        {
            slider.Minimum = min;
            slider.Maximum = max;
            slider.TickStyle = TickStyle.None;
            box.TextAlign = HorizontalAlignment.Right;
            slider.ValueChanged += (_, _) =>
            {
                if (_updatingUi) return;
                _updatingUi = true;
                box.Text = slider.Value.ToString();
                _updatingUi = false;
                if (_cam != IntPtr.Zero) apply(slider.Value);
            };
            WireNumericBox(box, v =>
            {
                int val = (int)Math.Clamp(Math.Round(v), min, max);
                SetSliderSafe(slider, val);
                _updatingUi = true;
                box.Text = val.ToString();
                _updatingUi = false;
                if (_cam != IntPtr.Zero) apply(val);
            });
            AddLabeledRow(name, slider, box);
        }

        Label Header(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 10, 0, 2),
        };

        // --- Camera ---
        AddFullRow(Header("Camera"));
        _resCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _resCombo.SelectedIndexChanged += OnResolutionChanged;
        AddLabeledRow("Size", _resCombo);
        _fpsLabel.Text = "—";
        _fpsLabel.AutoSize = true;
        AddLabeledRow("Frame rate", _fpsLabel);

        // --- Exposure ---
        AddFullRow(Header("Exposure"));

        _autoExpo.Text = "Auto exposure";
        _autoExpo.AutoSize = true;
        _autoExpo.CheckedChanged += OnAutoExpoChanged;
        AddFullRow(_autoExpo);

        AddIntSlider("Target", _aeTargetSlider, _aeTargetBox, 16, 220,
            v => AmcamNative.Amcam_put_AutoExpoTarget(_cam, (ushort)v));

        // "Goal time": the longest exposure auto mode may use. Directly sets
        // the achievable frame rate (fps ≈ 1000 / goal ms, up to the sensor
        // readout limit); auto exposure compensates with gain.
        AddIntSlider("Max ms", _maxAutoSlider, _maxAutoBox, 1, 200, v =>
        {
            _maxAutoUs = (uint)(v * 1000);
            AmcamNative.Amcam_put_MaxAutoExpoTimeAGain(_cam, _maxAutoUs, Math.Min(_gainMax, MaxAutoGainCap));
        });

        _expoSlider.Minimum = 0;
        _expoSlider.Maximum = SliderSteps;
        _expoSlider.TickStyle = TickStyle.None;
        _expoBox.TextAlign = HorizontalAlignment.Right;
        _expoSlider.ValueChanged += OnExposureSlider;
        AddLabeledRow("Time ms", _expoSlider, _expoBox);
        WireNumericBox(_expoBox, v =>
        {
            uint expoUs = (uint)Math.Clamp(Math.Round(v * 1000.0), _expoMinUs, _expoMaxUs);
            if (_cam == IntPtr.Zero) return;
            AmcamNative.Amcam_put_ExpoTime(_cam, expoUs);
            SetSliderSafe(_expoSlider, ExposureToSlider(expoUs));
            _updatingUi = true;
            SetExposureText(expoUs);
            _updatingUi = false;
        });

        AddIntSlider("Gain %", _gainSlider, _gainBox, 100, 300,
            v => AmcamNative.Amcam_put_ExpoAGain(_cam, (ushort)v));

        // --- Color ---
        AddFullRow(Header("Color"));

        _wbButton.Text = "Auto white balance";
        _wbButton.AutoSize = true;
        _wbButton.Click += OnWhiteBalance;
        AddFullRow(_wbButton);

        AddIntSlider("Temp", _wbTempSlider, _wbTempBox, AmcamNative.TEMP_MIN, AmcamNative.TEMP_MAX,
            _ => ApplyTempTint());
        AddIntSlider("Tint", _wbTintSlider, _wbTintBox, AmcamNative.TINT_MIN, AmcamNative.TINT_MAX,
            _ => ApplyTempTint());
        AddIntSlider("Gamma", _gammaSlider, _gammaBox, AmcamNative.GAMMA_MIN, AmcamNative.GAMMA_MAX,
            v => AmcamNative.Amcam_put_Gamma(_cam, v));
        AddIntSlider("Contrast", _contrastSlider, _contrastBox, AmcamNative.CONTRAST_MIN, AmcamNative.CONTRAST_MAX,
            v => AmcamNative.Amcam_put_Contrast(_cam, v));
        AddIntSlider("Saturate", _satSlider, _satBox, AmcamNative.SATURATION_MIN, AmcamNative.SATURATION_MAX,
            v => AmcamNative.Amcam_put_Saturation(_cam, v));

        _resetColorButton.Text = "Reset colors";
        _resetColorButton.AutoSize = true;
        _resetColorButton.Click += OnResetColors;
        AddFullRow(_resetColorButton);

        // --- Image ---
        AddFullRow(Header("Image"));

        AddIntSlider("Sharpen", _sharpSlider, _sharpBox, 0, 500,
            v => AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_SHARPENING, v == 0 ? 0 : (2 << 16) | v));

        _flickerCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _flickerCombo.Items.AddRange(new object[] { "50 Hz lighting", "60 Hz lighting", "DC lighting" });
        _flickerCombo.SelectedIndexChanged += OnFlickerChanged;
        AddLabeledRow("Flicker", _flickerCombo);

        // --- View ---
        AddFullRow(Header("View"));

        _flipH.Text = "Flip horizontal";
        _flipH.AutoSize = true;
        _flipH.CheckedChanged += (_, _) => { if (!_updatingUi && _cam != IntPtr.Zero) AmcamNative.Amcam_put_HFlip(_cam, _flipH.Checked ? 1 : 0); };
        AddFullRow(_flipH);

        _flipV.Text = "Flip vertical";
        _flipV.AutoSize = true;
        _flipV.CheckedChanged += (_, _) => { if (!_updatingUi && _cam != IntPtr.Zero) AmcamNative.Amcam_put_VFlip(_cam, _flipV.Checked ? 1 : 0); };
        AddFullRow(_flipV);

        _crosshair.Text = "Crosshair";
        _crosshair.AutoSize = true;
        _crosshair.CheckedChanged += (_, _) => { _view.Crosshair = _crosshair.Checked; _view.Invalidate(); };
        AddFullRow(_crosshair);

        var buttonRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        _freezeButton.Text = "Freeze";
        _freezeButton.AutoSize = true;
        _freezeButton.Click += OnFreeze;
        _snapshotButton.Text = "Snapshot";
        _snapshotButton.AutoSize = true;
        _snapshotButton.Click += OnSnapshot;
        _saveDefaultsButton.Text = "Save defaults";
        _saveDefaultsButton.AutoSize = true;
        _saveDefaultsButton.Click += OnSaveDefaults;
        buttonRow.Controls.Add(_freezeButton);
        buttonRow.Controls.Add(_snapshotButton);
        buttonRow.Controls.Add(_saveDefaultsButton);
        AddFullRow(buttonRow);

        SetConnectedControlsEnabled(false);
        return panel;
    }

    /// <summary>Numeric entry: committed on Enter or when leaving the box.</summary>
    private void WireNumericBox(TextBox box, Action<double> apply)
    {
        void Commit()
        {
            if (_updatingUi || !box.Enabled)
                return;
            var text = box.Text.Trim().Replace(',', '.');
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                apply(v);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                Commit();
            }
        };
        box.Leave += (_, _) => Commit();
    }

    private void SetSliderSafe(TrackBar slider, int value)
    {
        _updatingUi = true;
        slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        _updatingUi = false;
    }

    private void SetExposureText(uint expoUs) =>
        _expoBox.Text = (expoUs / 1000.0).ToString("0.0#", CultureInfo.CurrentCulture);

    private void ApplyTempTint() =>
        AmcamNative.Amcam_put_TempTint(_cam, _wbTempSlider.Value, _wbTintSlider.Value);

    // ---------- Connection ----------

    private void TryConnect()
    {
        if (_cam != IntPtr.Zero)
        {
            _reconnectTimer.Stop();
            return;
        }

        var devices = AmcamNative.Enumerate();
        if (devices.Length == 0)
        {
            _statusCamera.Text = "No camera found — retrying…";
            _reconnectTimer.Start();
            return;
        }

        var device = devices[0];
        _deviceName = device.displayname;
        _cam = AmcamNative.Amcam_Open(device.id);
        if (_cam == IntPtr.Zero)
        {
            _statusCamera.Text = $"Found {device.displayname} but failed to open — retrying…";
            _reconnectTimer.Start();
            return;
        }

        // Resolution must be selected before the stream starts. A pending
        // request (failed in-place switch) wins over the saved default.
        int desired = _pendingResolution >= 0 ? _pendingResolution : _settings?.ResolutionIndex ?? -1;
        _pendingResolution = -1;
        if (desired >= 0 && desired < AmcamNative.Amcam_get_ResolutionNumber(_cam))
            AmcamNative.Amcam_put_eSize(_cam, (uint)desired);

        PopulateResolutions();

        if (_settings is { } s0)
            _maxAutoUs = Math.Clamp(s0.MaxAutoExpoUs, 1_000, 1_000_000);

        if (!StartStream())
        {
            CloseCamera();
            _statusCamera.Text = "Failed to start camera stream — retrying…";
            _reconnectTimer.Start();
            return;
        }

        if (_settings is { } s)
        {
            ApplySavedSettings(s);
        }
        else
        {
            // No saved white balance — do an automatic one-push correction.
            AmcamNative.Amcam_AwbOnce(_cam, IntPtr.Zero, IntPtr.Zero);
        }

        SyncUiFromCamera();
        _reconnectTimer.Stop();
        _frozen = false;
        _freezeButton.Text = "Freeze";
        SetConnectedControlsEnabled(true);
        UpdateConnectedStatus();
    }

    /// <summary>
    /// Configure and start streaming on the already-open camera. Used for
    /// both initial connect and resolution changes (after Amcam_Stop).
    /// </summary>
    private bool StartStream()
    {
        if (!AmcamNative.Ok(AmcamNative.Amcam_get_Size(_cam, out int width, out int height)))
            return false;

        ReadRanges();
        ApplyStartupDefaults();

        _frame?.Dispose();
        _frame = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        _view.Frame = _frame;
        _view.Invalidate();

        _callback ??= OnCameraEvent;
        return AmcamNative.Ok(AmcamNative.Amcam_StartPullModeWithCallback(_cam, _callback, IntPtr.Zero));
    }

    /// <summary>Startup configuration for crisp, low-latency live view.</summary>
    private void ApplyStartupDefaults()
    {
        // Max USB bandwidth / frame speed level (not guaranteed to be the default).
        AmcamNative.Amcam_put_Speed(_cam, AmcamNative.Amcam_get_MaxSpeed(_cam));
        // Lowest latency: always show the newest frame, drop stale buffered
        // frames, shortest frame queues, prioritized USB grab thread.
        AmcamNative.Amcam_put_RealTime(_cam, 1);
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_FRAME_DEQUE_LENGTH, 2);
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_BACKEND_DEQUE_LENGTH, 2);
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_THREAD_PRIORITY, 2);
        // Start in auto exposure, capped at the user's "goal time" so auto
        // exposure raises gain instead of dropping the frame rate (the SDK
        // default allows up to 350 ms ≈ 3 fps).
        AmcamNative.Amcam_put_AutoExpoEnable(_cam, 1);
        AmcamNative.Amcam_put_MaxAutoExpoTimeAGain(_cam, _maxAutoUs, Math.Min(_gainMax, MaxAutoGainCap));
        // Stabilize auto exposure so it doesn't hunt on specular solder
        // reflections: meter the whole frame instead of the brightest 1%,
        // ignore small brightness changes, and damp the transitions.
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_AUTOEXPOSURE_PERCENT, 100);
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_AUTOEXP_THRESHOLD, 10);
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_AUTOEXP_EXPOTIME_DAMP, 300);
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_AUTOEXP_GAIN_DAMP, 300);
        // Anti-flicker default DC: AC modes quantize exposure to the mains
        // half-period (10 ms at 50 Hz), and this camera's firmware AE cannot
        // settle between quanta — it oscillates between the two nearest
        // exposure steps (~6x/s brightness pumping, observed on the MU503
        // under every AUTOEXP_POLICY). Microscope LED illuminators are DC,
        // so continuous exposure is the right default; the Flicker selector
        // remains for AC-lit setups.
        AmcamNative.Amcam_put_HZ(_cam, 2);
        // EA (Edge Aware) demosaic: the default bilinear demosaic produces
        // rainbow false color along fine PCB traces and pin edges (verified
        // by A/B frame captures). EA removes it at the lowest CPU cost of
        // the SDK's algorithms (AHD was equally clean but ~95% of a core at
        // 1280×960/36 fps).
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_DEMOSAIC, 4);
        // Moderate sharpening: radius 2, threshold 0.
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_SHARPENING, (2 << 16) | DefaultSharpness);
    }

    private void ApplySavedSettings(AppSettings s)
    {
        AmcamNative.Amcam_put_HZ(_cam, s.FlickerIndex switch { 0 => 1, 1 => 0, _ => 2 });
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_SHARPENING,
            s.Sharpen == 0 ? 0 : (2 << 16) | Math.Clamp(s.Sharpen, 0, 500));
        AmcamNative.Amcam_put_HFlip(_cam, s.FlipH ? 1 : 0);
        AmcamNative.Amcam_put_VFlip(_cam, s.FlipV ? 1 : 0);
        AmcamNative.Amcam_put_AutoExpoTarget(_cam, (ushort)Math.Clamp(s.AeTarget, 16, 220));
        AmcamNative.Amcam_put_Gamma(_cam, Math.Clamp(s.Gamma, AmcamNative.GAMMA_MIN, AmcamNative.GAMMA_MAX));
        AmcamNative.Amcam_put_Contrast(_cam, Math.Clamp(s.Contrast, AmcamNative.CONTRAST_MIN, AmcamNative.CONTRAST_MAX));
        AmcamNative.Amcam_put_Saturation(_cam, Math.Clamp(s.Saturation, AmcamNative.SATURATION_MIN, AmcamNative.SATURATION_MAX));
        if (s.WbTemp >= AmcamNative.TEMP_MIN && s.WbTint >= AmcamNative.TINT_MIN)
            AmcamNative.Amcam_put_TempTint(_cam,
                Math.Clamp(s.WbTemp, AmcamNative.TEMP_MIN, AmcamNative.TEMP_MAX),
                Math.Clamp(s.WbTint, AmcamNative.TINT_MIN, AmcamNative.TINT_MAX));
        else
            AmcamNative.Amcam_AwbOnce(_cam, IntPtr.Zero, IntPtr.Zero);

        if (s.AutoExposure)
        {
            AmcamNative.Amcam_put_AutoExpoEnable(_cam, 1);
        }
        else
        {
            AmcamNative.Amcam_put_AutoExpoEnable(_cam, 0);
            AmcamNative.Amcam_put_ExpoTime(_cam, Math.Clamp(s.ExposureUs, _expoMinUs, _expoMaxUs));
            AmcamNative.Amcam_put_ExpoAGain(_cam, (ushort)Math.Clamp(s.GainPercent, _gainMin, _gainMax));
        }

        _updatingUi = true;
        _sharpSlider.Value = Math.Clamp(s.Sharpen, 0, 500);
        _sharpBox.Text = _sharpSlider.Value.ToString();
        _flickerCombo.SelectedIndex = Math.Clamp(s.FlickerIndex, 0, 2);
        _flipH.Checked = s.FlipH;
        _flipV.Checked = s.FlipV;
        _crosshair.Checked = s.Crosshair;
        _view.Crosshair = s.Crosshair;
        _updatingUi = false;
    }

    private void PopulateResolutions()
    {
        _updatingUi = true;
        _resCombo.Items.Clear();
        int count = AmcamNative.Amcam_get_ResolutionNumber(_cam);
        for (uint i = 0; i < count; i++)
        {
            if (AmcamNative.Ok(AmcamNative.Amcam_get_Resolution(_cam, i, out int w, out int h)))
                _resCombo.Items.Add($"{w} × {h}");
        }
        if (AmcamNative.Ok(AmcamNative.Amcam_get_eSize(_cam, out uint current)) && current < _resCombo.Items.Count)
            _resCombo.SelectedIndex = (int)current;
        _updatingUi = false;
    }

    private void OnResolutionChanged(object? sender, EventArgs e)
    {
        if (_updatingUi || _cam == IntPtr.Zero || _resCombo.SelectedIndex < 0)
            return;

        AmcamNative.Amcam_Stop(_cam);
        bool ok = AmcamNative.Ok(AmcamNative.Amcam_put_eSize(_cam, (uint)_resCombo.SelectedIndex))
                  && StartStream();
        if (!ok)
        {
            // Couldn't switch in place — fall back to a full reconnect.
            _pendingResolution = _resCombo.SelectedIndex;
            CloseCamera();
            _statusCamera.Text = "Switching resolution…";
            _reconnectTimer.Start();
            return;
        }
        _frozen = false;
        _freezeButton.Text = "Freeze";
        SyncUiFromCamera();
        UpdateConnectedStatus();
    }

    private void UpdateConnectedStatus()
    {
        AmcamNative.Amcam_get_Size(_cam, out int w, out int h);
        _statusCamera.Text = $"Connected: {_deviceName}  ({w}×{h})";
    }

    private void CloseCamera()
    {
        if (_cam == IntPtr.Zero)
            return;
        var cam = _cam;
        _cam = IntPtr.Zero;
        AmcamNative.Amcam_Close(cam);
        _callback = null;
        SetConnectedControlsEnabled(false);
        _fpsLabel.Text = "—";
    }

    private void OnDisconnected()
    {
        CloseCamera();
        _statusCamera.Text = "Camera disconnected — waiting…";
        _statusFps.Text = "";
        _reconnectTimer.Start();
    }

    private void ReadRanges()
    {
        AmcamNative.Amcam_get_ExpTimeRange(_cam, out _expoMinUs, out _expoMaxUs, out _);
        if (_expoMinUs < 1) _expoMinUs = 1;
        if (_expoMaxUs <= _expoMinUs) _expoMaxUs = _expoMinUs + 1;

        AmcamNative.Amcam_get_ExpoAGainRange(_cam, out _gainMin, out _gainMax, out _);
        _updatingUi = true;
        _gainSlider.Minimum = _gainMin;
        _gainSlider.Maximum = Math.Max(_gainMax, (ushort)(_gainMin + 1));
        _updatingUi = false;
    }

    private void SyncUiFromCamera()
    {
        _updatingUi = true;
        AmcamNative.Amcam_get_AutoExpoEnable(_cam, out int auto);
        _autoExpo.Checked = auto != 0;
        if (AmcamNative.Ok(AmcamNative.Amcam_get_AutoExpoTarget(_cam, out ushort target)))
        {
            _aeTargetSlider.Value = Math.Clamp(target, (ushort)_aeTargetSlider.Minimum, (ushort)_aeTargetSlider.Maximum);
            _aeTargetBox.Text = target.ToString();
        }
        int maxMs = (int)(_maxAutoUs / 1000);
        _maxAutoSlider.Value = Math.Clamp(maxMs, _maxAutoSlider.Minimum, _maxAutoSlider.Maximum);
        _maxAutoBox.Text = maxMs.ToString();
        _sharpBox.Text = _sharpSlider.Value.ToString();
        if (_flickerCombo.SelectedIndex < 0)
            _flickerCombo.SelectedIndex = 2; // DC (matches ApplyStartupDefaults)
        _updatingUi = false;
        UpdateExposureControlsEnabled();
        RefreshSlidersFromCamera();
        RefreshWhiteBalanceFromCamera();
        RefreshColorFromCamera();
    }

    private void RefreshSlidersFromCamera()
    {
        if (_cam == IntPtr.Zero)
            return;
        _updatingUi = true;
        if (AmcamNative.Ok(AmcamNative.Amcam_get_ExpoTime(_cam, out uint expoUs)))
        {
            _expoSlider.Value = ExposureToSlider(expoUs);
            SetExposureText(expoUs);
        }
        if (AmcamNative.Ok(AmcamNative.Amcam_get_ExpoAGain(_cam, out ushort gain)))
        {
            _gainSlider.Value = Math.Clamp(gain, (ushort)_gainSlider.Minimum, (ushort)_gainSlider.Maximum);
            _gainBox.Text = gain.ToString();
        }
        _updatingUi = false;
    }

    private void RefreshWhiteBalanceFromCamera()
    {
        if (_cam == IntPtr.Zero)
            return;
        if (AmcamNative.Ok(AmcamNative.Amcam_get_TempTint(_cam, out int temp, out int tint)))
        {
            _updatingUi = true;
            _wbTempSlider.Value = Math.Clamp(temp, AmcamNative.TEMP_MIN, AmcamNative.TEMP_MAX);
            _wbTempBox.Text = temp.ToString();
            _wbTintSlider.Value = Math.Clamp(tint, AmcamNative.TINT_MIN, AmcamNative.TINT_MAX);
            _wbTintBox.Text = tint.ToString();
            _updatingUi = false;
        }
    }

    private void RefreshColorFromCamera()
    {
        if (_cam == IntPtr.Zero)
            return;
        _updatingUi = true;
        if (AmcamNative.Ok(AmcamNative.Amcam_get_Gamma(_cam, out int gamma)))
        {
            _gammaSlider.Value = Math.Clamp(gamma, AmcamNative.GAMMA_MIN, AmcamNative.GAMMA_MAX);
            _gammaBox.Text = gamma.ToString();
        }
        if (AmcamNative.Ok(AmcamNative.Amcam_get_Contrast(_cam, out int contrast)))
        {
            _contrastSlider.Value = Math.Clamp(contrast, AmcamNative.CONTRAST_MIN, AmcamNative.CONTRAST_MAX);
            _contrastBox.Text = contrast.ToString();
        }
        if (AmcamNative.Ok(AmcamNative.Amcam_get_Saturation(_cam, out int sat)))
        {
            _satSlider.Value = Math.Clamp(sat, AmcamNative.SATURATION_MIN, AmcamNative.SATURATION_MAX);
            _satBox.Text = sat.ToString();
        }
        _updatingUi = false;
    }

    // ---------- Camera events (callback arrives on an SDK thread) ----------

    private void OnCameraEvent(uint nEvent, IntPtr ctx)
    {
        try
        {
            switch (nEvent)
            {
                case AmcamNative.EVENT_IMAGE:
                    // Coalesce: if the UI hasn't processed the previous frame
                    // yet, drop this event — PullImageV2 always returns the
                    // newest frame anyway.
                    if (Interlocked.CompareExchange(ref _framePending, 1, 0) == 0)
                        BeginInvoke(PullAndShowFrame);
                    break;
                case AmcamNative.EVENT_EXPOSURE:
                    BeginInvoke(RefreshSlidersFromCamera);
                    break;
                case AmcamNative.EVENT_TEMPTINT:
                    BeginInvoke(RefreshWhiteBalanceFromCamera);
                    break;
                case AmcamNative.EVENT_ERROR:
                case AmcamNative.EVENT_DISCONNECTED:
                    BeginInvoke(OnDisconnected);
                    break;
            }
        }
        catch
        {
            // Never let an exception cross the native callback boundary
            // (e.g. BeginInvoke racing window teardown).
        }
    }

    private void PullAndShowFrame()
    {
        Interlocked.Exchange(ref _framePending, 0);
        if (_cam == IntPtr.Zero || _frame is null)
            return;

        var info = new AmcamNative.FrameInfoV2();
        var rect = new Rectangle(0, 0, _frame.Width, _frame.Height);
        var data = _frame.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            if (!AmcamNative.Ok(AmcamNative.Amcam_PullImageV2(_cam, data.Scan0, 24, ref info)))
                return;
        }
        finally
        {
            _frame.UnlockBits(data);
        }
        _view.Invalidate();

        _fpsFrames++;
        if (_fpsClock.ElapsedMilliseconds >= 1000)
        {
            string fps = $"{_fpsFrames * 1000.0 / _fpsClock.ElapsedMilliseconds:F1} fps";
            _statusFps.Text = fps;
            _fpsLabel.Text = fps;
            _fpsFrames = 0;
            _fpsClock.Restart();
        }
    }

    // ---------- Controls ----------

    private void SetConnectedControlsEnabled(bool enabled)
    {
        _resCombo.Enabled = enabled;
        _autoExpo.Enabled = enabled;
        _wbButton.Enabled = enabled;
        _wbTempSlider.Enabled = _wbTempBox.Enabled = enabled;
        _wbTintSlider.Enabled = _wbTintBox.Enabled = enabled;
        _gammaSlider.Enabled = _gammaBox.Enabled = enabled;
        _contrastSlider.Enabled = _contrastBox.Enabled = enabled;
        _satSlider.Enabled = _satBox.Enabled = enabled;
        _resetColorButton.Enabled = enabled;
        _sharpSlider.Enabled = _sharpBox.Enabled = enabled;
        _flickerCombo.Enabled = enabled;
        _flipH.Enabled = enabled;
        _flipV.Enabled = enabled;
        _freezeButton.Enabled = enabled;
        _saveDefaultsButton.Enabled = enabled;
        _snapshotButton.Enabled = enabled || _frame is not null;
        if (enabled)
            UpdateExposureControlsEnabled();
        else
        {
            _aeTargetSlider.Enabled = _aeTargetBox.Enabled = false;
            _maxAutoSlider.Enabled = _maxAutoBox.Enabled = false;
            _expoSlider.Enabled = _expoBox.Enabled = false;
            _gainSlider.Enabled = _gainBox.Enabled = false;
        }
    }

    private void UpdateExposureControlsEnabled()
    {
        bool auto = _autoExpo.Checked;
        _aeTargetSlider.Enabled = _aeTargetBox.Enabled = auto;
        _maxAutoSlider.Enabled = _maxAutoBox.Enabled = auto;
        _expoSlider.Enabled = _expoBox.Enabled = !auto;
        bool gainOk = !auto && _gainMax > _gainMin;
        _gainSlider.Enabled = _gainBox.Enabled = gainOk;
    }

    private void OnAutoExpoChanged(object? sender, EventArgs e)
    {
        if (_updatingUi || _cam == IntPtr.Zero)
            return;
        AmcamNative.Amcam_put_AutoExpoEnable(_cam, _autoExpo.Checked ? 1 : 0);
        UpdateExposureControlsEnabled();
        if (!_autoExpo.Checked)
            RefreshSlidersFromCamera(); // sliders take over from the last auto values
    }

    private void OnExposureSlider(object? sender, EventArgs e)
    {
        if (_updatingUi || _cam == IntPtr.Zero)
            return;
        uint expoUs = SliderToExposure(_expoSlider.Value);
        AmcamNative.Amcam_put_ExpoTime(_cam, expoUs);
        _updatingUi = true;
        SetExposureText(expoUs);
        _updatingUi = false;
    }

    private void OnWhiteBalance(object? sender, EventArgs e)
    {
        if (_cam == IntPtr.Zero)
            return;
        // Result arrives via EVENT_TEMPTINT, which updates the sliders.
        AmcamNative.Amcam_AwbOnce(_cam, IntPtr.Zero, IntPtr.Zero);
        _statusCamera.Text = "White balance adjusted";
    }

    private void OnResetColors(object? sender, EventArgs e)
    {
        if (_cam == IntPtr.Zero)
            return;
        AmcamNative.Amcam_put_Gamma(_cam, AmcamNative.GAMMA_DEF);
        AmcamNative.Amcam_put_Contrast(_cam, AmcamNative.CONTRAST_DEF);
        AmcamNative.Amcam_put_Saturation(_cam, AmcamNative.SATURATION_DEF);
        AmcamNative.Amcam_put_TempTint(_cam, AmcamNative.TEMP_DEF, AmcamNative.TINT_DEF);
        RefreshColorFromCamera();
        RefreshWhiteBalanceFromCamera();
    }

    private void OnFlickerChanged(object? sender, EventArgs e)
    {
        if (_updatingUi || _cam == IntPtr.Zero)
            return;
        int hz = _flickerCombo.SelectedIndex switch
        {
            0 => 1, // 50 Hz AC
            1 => 0, // 60 Hz AC
            _ => 2, // DC
        };
        AmcamNative.Amcam_put_HZ(_cam, hz);
        if (hz != 2 && _autoExpo.Checked)
            _statusCamera.Text = "Note: in AC mode auto exposure steps coarsely — prefer manual exposure";
    }

    private void OnFreeze(object? sender, EventArgs e)
    {
        if (_cam == IntPtr.Zero)
            return;
        _frozen = !_frozen;
        AmcamNative.Amcam_Pause(_cam, _frozen ? 1 : 0);
        _freezeButton.Text = _frozen ? "Resume" : "Freeze";
        _statusFps.Text = _frozen ? "frozen" : "";
    }

    private void OnSnapshot(object? sender, EventArgs e)
    {
        if (_frame is null)
            return;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScopeCAM");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"scopecam_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        using (var copy = new Bitmap(_frame))
            copy.Save(path, ImageFormat.Png);
        _statusCamera.Text = $"Saved {path}";
    }

    private void OnSaveDefaults(object? sender, EventArgs e)
    {
        if (_cam == IntPtr.Zero)
            return;
        AmcamNative.Amcam_get_ExpoTime(_cam, out uint expoUs);
        AmcamNative.Amcam_get_ExpoAGain(_cam, out ushort gain);
        AmcamNative.Amcam_get_TempTint(_cam, out int temp, out int tint);
        var s = new AppSettings
        {
            ResolutionIndex = _resCombo.SelectedIndex,
            AutoExposure = _autoExpo.Checked,
            AeTarget = _aeTargetSlider.Value,
            MaxAutoExpoUs = _maxAutoUs,
            ExposureUs = expoUs,
            GainPercent = gain,
            WbTemp = temp,
            WbTint = tint,
            Gamma = _gammaSlider.Value,
            Contrast = _contrastSlider.Value,
            Saturation = _satSlider.Value,
            Sharpen = _sharpSlider.Value,
            FlickerIndex = _flickerCombo.SelectedIndex,
            FlipH = _flipH.Checked,
            FlipV = _flipV.Checked,
            Crosshair = _crosshair.Checked,
        };
        try
        {
            s.Save();
            _settings = s;
            _statusCamera.Text = "Defaults saved — applied on every start";
        }
        catch (Exception ex)
        {
            _statusCamera.Text = $"Could not save defaults: {ex.Message}";
        }
    }

    // ---------- Exposure slider mapping ----------
    // Exposure ranges span several orders of magnitude (microseconds to
    // seconds), so the slider is logarithmic.

    private uint SliderToExposure(int sliderValue)
    {
        double t = sliderValue / (double)SliderSteps;
        double expo = _expoMinUs * Math.Pow(_expoMaxUs / (double)_expoMinUs, t);
        return (uint)Math.Clamp(Math.Round(expo), _expoMinUs, _expoMaxUs);
    }

    private int ExposureToSlider(uint expoUs)
    {
        double clamped = Math.Clamp(expoUs, _expoMinUs, _expoMaxUs);
        double t = Math.Log(clamped / _expoMinUs) / Math.Log(_expoMaxUs / (double)_expoMinUs);
        return (int)Math.Clamp(Math.Round(t * SliderSteps), 0, SliderSteps);
    }
}
