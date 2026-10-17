using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;

namespace ScopeCAM;

public class MainForm : Form
{
    private const int SliderSteps = 1000;        // exposure slider resolution (log scale)
    private const ushort MaxAutoGainCap = 500;   // don't let auto exposure go noisier than 5x
    private const uint MaxAutoUsCeiling = 200_000; // "Max ms" slider upper bound, in µs

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
    private readonly TrackBar _denoiseSlider = new();
    private readonly TextBox _denoiseBox = new();
    private readonly TrackBar _smoothSlider = new();
    private readonly TextBox _smoothBox = new();
    private readonly ComboBox _demosaicCombo = new();
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
    private readonly System.Windows.Forms.Timer _statusRestoreTimer = new();

    private IntPtr _cam = IntPtr.Zero;
    private AmcamNative.EventCallback? _callback; // GC-rooted for the native side
    private Bitmap? _frame;
    private int _framePending; // coalesce image events so the UI thread never backlogs
    private bool _frozen;
    private string _deviceName = "";
    private AppSettings? _settings = AppSettings.Load();
    private int _pendingResolution = -1; // resolution to apply on next connect
    private uint _maxAutoUs = 33_333;    // auto-exposure time cap ("goal time")

    // Temporal frame averaging (the "Smooth" control).
    private int _smoothLevel;            // 0 = off, 1..4 = EMA strength
    private byte[] _pullBuf = Array.Empty<byte>();
    private ushort[] _smoothAcc = Array.Empty<ushort>();
    private bool _smoothAccValid;

    private uint _expoMinUs = 1, _expoMaxUs = 1;
    private ushort _gainMin = 100, _gainMax = 100;
    private bool _updatingUi; // true while controls are being set from camera values

    private readonly Stopwatch _fpsClock = Stopwatch.StartNew();
    private int _fpsFrames;

    public MainForm()
    {
        Text = "ScopeCAM";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1280, 880);
        MinimumSize = new Size(820, 700);

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

        _statusRestoreTimer.Interval = 4000;
        _statusRestoreTimer.Tick += (_, _) =>
        {
            _statusRestoreTimer.Stop();
            if (_cam != IntPtr.Zero)
                UpdateConnectedStatus();
        };

        Load += (_, _) => TryConnect();
        FormClosing += (_, _) => CloseCamera();
    }

    /// <summary>Show a transient status message, then restore the Connected line.</summary>
    private void FlashStatus(string message)
    {
        _statusCamera.Text = message;
        _statusRestoreTimer.Stop();
        _statusRestoreTimer.Start();
    }

    private Control BuildControlPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 310,
            AutoScroll = true,
            Padding = new Padding(8, 2, 8, 2),
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
            middle.Margin = new Padding(3, 1, 3, 1);
            panel.Controls.Add(middle, 1, row);
            if (right is not null)
            {
                right.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                right.Margin = new Padding(3, 1, 3, 1);
                panel.Controls.Add(right, 2, row);
            }
            else
            {
                panel.SetColumnSpan(middle, 2);
            }
        }

        // Standard slider row: int slider + numeric box, committed either way.
        // The box clamps against the slider's LIVE bounds (ReadRanges may
        // widen them after construction).
        void AddIntSlider(string name, TrackBar slider, TextBox box, int min, int max, Action<int> apply)
        {
            slider.Minimum = min;
            slider.Maximum = max;
            slider.TickStyle = TickStyle.None;
            slider.AutoSize = false;
            slider.Height = 26;
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
                int val = (int)Math.Clamp(Math.Round(v), slider.Minimum, slider.Maximum);
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
            Margin = new Padding(0, 7, 0, 1),
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

        // "Goal time": the longest exposure auto mode may use. Sets the frame
        // rate floor (fps ≈ 1000 / goal ms, up to the sensor readout limit);
        // auto exposure compensates with gain.
        AddIntSlider("Max ms", _maxAutoSlider, _maxAutoBox, 1, (int)(MaxAutoUsCeiling / 1000), v =>
        {
            _maxAutoUs = (uint)(v * 1000);
            AmcamNative.Amcam_put_MaxAutoExpoTimeAGain(_cam, _maxAutoUs, Math.Min(_gainMax, MaxAutoGainCap));
        });

        _expoSlider.Minimum = 0;
        _expoSlider.Maximum = SliderSteps;
        _expoSlider.TickStyle = TickStyle.None;
        _expoSlider.AutoSize = false;
        _expoSlider.Height = 26;
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
            v => AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_SHARPENING, SharpenValue(v)));

        AddIntSlider("Denoise", _denoiseSlider, _denoiseBox, 0, 100,
            v => AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_DENOISE, v));

        // App-side temporal averaging; no camera call.
        AddIntSlider("Smooth", _smoothSlider, _smoothBox, 0, 4, _ => { });
        _smoothSlider.ValueChanged += (_, _) => { _smoothLevel = _smoothSlider.Value; _smoothAccValid = false; };

        _demosaicCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _demosaicCombo.Items.AddRange(new object[]
            { "EA (fast)", "AHD (best)", "VNG", "PPG", "Bilinear" });
        _demosaicCombo.SelectedIndexChanged += (_, _) =>
        {
            if (!_updatingUi && _cam != IntPtr.Zero)
                AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_DEMOSAIC, DemosaicSdkValue(_demosaicCombo.SelectedIndex));
        };
        AddLabeledRow("Demosaic", _demosaicCombo);

        _flickerCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _flickerCombo.Items.AddRange(new object[] { "50 Hz lighting", "60 Hz lighting", "DC lighting" });
        _flickerCombo.SelectedIndexChanged += OnFlickerChanged;
        AddLabeledRow("Flicker", _flickerCombo);

        // --- View ---
        AddFullRow(Header("View"));

        _flipH.Text = "Flip horizontal";
        _flipH.AutoSize = true;
        _flipH.Margin = new Padding(3, 1, 3, 1);
        _flipH.CheckedChanged += (_, _) => { if (!_updatingUi && _cam != IntPtr.Zero) AmcamNative.Amcam_put_HFlip(_cam, _flipH.Checked ? 1 : 0); };
        AddFullRow(_flipH);

        _flipV.Text = "Flip vertical";
        _flipV.AutoSize = true;
        _flipV.Margin = new Padding(3, 1, 3, 1);
        _flipV.CheckedChanged += (_, _) => { if (!_updatingUi && _cam != IntPtr.Zero) AmcamNative.Amcam_put_VFlip(_cam, _flipV.Checked ? 1 : 0); };
        AddFullRow(_flipV);

        _crosshair.Text = "Crosshair";
        _crosshair.AutoSize = true;
        _crosshair.Margin = new Padding(3, 1, 3, 1);
        _crosshair.CheckedChanged += (_, _) => { _view.Crosshair = _crosshair.Checked; _view.Invalidate(); };
        AddFullRow(_crosshair);

        var buttonRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
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

        // Initial control values = the built-in defaults. These are the
        // single source of truth pushed to the camera by ApplyUiStateToCamera.
        _updatingUi = true;
        _autoExpo.Checked = true;
        _aeTargetSlider.Value = 120; _aeTargetBox.Text = "120";
        _maxAutoSlider.Value = 33; _maxAutoBox.Text = "33";
        _gainSlider.Value = 100; _gainBox.Text = "100";
        _wbTempSlider.Value = AmcamNative.TEMP_DEF; _wbTempBox.Text = AmcamNative.TEMP_DEF.ToString();
        _wbTintSlider.Value = AmcamNative.TINT_DEF; _wbTintBox.Text = AmcamNative.TINT_DEF.ToString();
        _gammaSlider.Value = AmcamNative.GAMMA_DEF; _gammaBox.Text = AmcamNative.GAMMA_DEF.ToString();
        _contrastSlider.Value = AmcamNative.CONTRAST_DEF; _contrastBox.Text = AmcamNative.CONTRAST_DEF.ToString();
        _satSlider.Value = AmcamNative.SATURATION_DEF; _satBox.Text = AmcamNative.SATURATION_DEF.ToString();
        _sharpSlider.Value = 150; _sharpBox.Text = "150";
        _denoiseSlider.Value = 0; _denoiseBox.Text = "0";
        _smoothSlider.Value = 0; _smoothBox.Text = "0";
        _demosaicCombo.SelectedIndex = 0; // EA
        _flickerCombo.SelectedIndex = 2;  // DC
        _updatingUi = false;

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
            var text = box.Text.Trim();
            // Accept the local decimal separator first, then fall back to
            // '.'/',' interchange for the other convention.
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double v) ||
                double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
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

    // Sharpening: radius 2, threshold 16 — the threshold keeps flat (noisy)
    // areas untouched so sharpening doesn't amplify pixel-level speckle.
    private static int SharpenValue(int strength) =>
        strength == 0 ? 0 : (16 << 24) | (2 << 16) | strength;

    // Combo order (by usefulness) → SDK demosaic ids.
    private static int DemosaicSdkValue(int comboIndex) =>
        comboIndex switch { 0 => 4, 1 => 3, 2 => 1, 3 => 2, _ => 0 };

    private static int FlickerSdkValue(int comboIndex) =>
        comboIndex switch { 0 => 1, 1 => 0, _ => 2 };

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

        if (!StartStream())
        {
            CloseCamera();
            _statusCamera.Text = "Failed to start camera stream — retrying…";
            _reconnectTimer.Start();
            return;
        }

        bool hasSavedWb = false;
        if (_settings is { } s)
        {
            SetUiFromSettings(s);
            hasSavedWb = s.WbTemp >= AmcamNative.TEMP_MIN && s.WbTint >= AmcamNative.TINT_MIN;
        }

        ApplyUiStateToCamera(_settings?.ExposureUs, (ushort?)_settings?.GainPercent);

        // Automatic color: one-push white balance unless a saved white
        // balance was just applied (ApplyUiStateToCamera pushed the sliders).
        if (!hasSavedWb)
            AmcamNative.Amcam_AwbOnce(_cam, IntPtr.Zero, IntPtr.Zero);

        SyncUiFromCamera();
        _reconnectTimer.Stop();
        _frozen = false;
        _freezeButton.Text = "Freeze";
        SetConnectedControlsEnabled(true);
        UpdateConnectedStatus();
    }

    /// <summary>
    /// Allocate buffers and start streaming on the already-open camera, with
    /// the fixed pipeline configuration. Used for both initial connect and
    /// resolution changes (after Amcam_Stop). User-adjustable settings are
    /// NOT touched here — ApplyUiStateToCamera pushes those afterwards.
    /// </summary>
    private bool StartStream()
    {
        if (!AmcamNative.Ok(AmcamNative.Amcam_get_Size(_cam, out int width, out int height)))
            return false;

        ReadRanges();
        ApplyPipelineConfig();

        _frame?.Dispose();
        _frame = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        _view.Frame = _frame;
        _view.Invalidate();

        int stride = (width * 3 + 3) & ~3;
        _pullBuf = new byte[stride * height];
        _smoothAcc = new ushort[stride * height];
        _smoothAccValid = false;
        Interlocked.Exchange(ref _framePending, 0);

        _callback ??= OnCameraEvent;
        return AmcamNative.Ok(AmcamNative.Amcam_StartPullModeWithCallback(_cam, _callback, IntPtr.Zero));
    }

    /// <summary>
    /// Fixed transport/AE-stability configuration. Safe to re-apply on every
    /// stream start; contains no user-adjustable settings.
    /// </summary>
    private void ApplyPipelineConfig()
    {
        // Max USB bandwidth / frame speed level (not guaranteed to be the default).
        AmcamNative.Amcam_put_Speed(_cam, AmcamNative.Amcam_get_MaxSpeed(_cam));
        // Lowest latency: always show the newest frame, drop stale buffered
        // frames, shortest frame queues, prioritized USB grab thread.
        AmcamNative.Amcam_put_RealTime(_cam, 1);
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_FRAME_DEQUE_LENGTH, 2);
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_BACKEND_DEQUE_LENGTH, 2);
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_THREAD_PRIORITY, 2);
        // Stabilize auto exposure so it doesn't hunt on specular solder
        // reflections: meter the whole frame instead of the brightest 1%,
        // ignore small brightness changes, and damp the transitions.
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_AUTOEXPOSURE_PERCENT, 100);
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_AUTOEXP_THRESHOLD, 10);
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_AUTOEXP_EXPOTIME_DAMP, 300);
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_AUTOEXP_GAIN_DAMP, 300);
    }

    /// <summary>
    /// Push every user-adjustable setting from the UI controls to the
    /// camera. The controls are the single source of truth, so this is safe
    /// after connect, reconnect, and resolution switches alike. Manual
    /// exposure/gain can be overridden (e.g. values captured before a
    /// resolution switch, or loaded from saved defaults).
    /// </summary>
    private void ApplyUiStateToCamera(uint? manualExpoUs = null, ushort? manualGain = null)
    {
        if (_cam == IntPtr.Zero)
            return;

        AmcamNative.Amcam_put_AutoExpoTarget(_cam, (ushort)_aeTargetSlider.Value);
        AmcamNative.Amcam_put_MaxAutoExpoTimeAGain(_cam, _maxAutoUs, Math.Min(_gainMax, MaxAutoGainCap));
        AmcamNative.Amcam_put_HZ(_cam, FlickerSdkValue(_flickerCombo.SelectedIndex));
        // Anti-flicker note: DC is the default because AC modes quantize
        // exposure to the mains half-period and this camera's firmware AE
        // oscillates between quanta (observed on the MU503).
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_DEMOSAIC, DemosaicSdkValue(_demosaicCombo.SelectedIndex));
        // EA demosaic default: the SDK's bilinear default produces rainbow
        // false color on fine PCB detail (verified by A/B captures).
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_DENOISE, _denoiseSlider.Value);
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_SHARPENING, SharpenValue(_sharpSlider.Value));
        AmcamNative.Amcam_put_HFlip(_cam, _flipH.Checked ? 1 : 0);
        AmcamNative.Amcam_put_VFlip(_cam, _flipV.Checked ? 1 : 0);
        AmcamNative.Amcam_put_Gamma(_cam, _gammaSlider.Value);
        AmcamNative.Amcam_put_Contrast(_cam, _contrastSlider.Value);
        AmcamNative.Amcam_put_Saturation(_cam, _satSlider.Value);
        AmcamNative.Amcam_put_TempTint(_cam, _wbTempSlider.Value, _wbTintSlider.Value);

        if (_autoExpo.Checked)
        {
            AmcamNative.Amcam_put_AutoExpoEnable(_cam, 1);
        }
        else
        {
            AmcamNative.Amcam_put_AutoExpoEnable(_cam, 0);
            uint expoUs = manualExpoUs ?? SliderToExposure(_expoSlider.Value);
            AmcamNative.Amcam_put_ExpoTime(_cam, Math.Clamp(expoUs, _expoMinUs, _expoMaxUs));
            ushort gain = manualGain ?? (ushort)_gainSlider.Value;
            AmcamNative.Amcam_put_ExpoAGain(_cam, Math.Clamp(gain, _gainMin, _gainMax));
        }

        _smoothLevel = _smoothSlider.Value;
    }

    /// <summary>Load saved defaults into the UI controls (no camera calls).</summary>
    private void SetUiFromSettings(AppSettings s)
    {
        _maxAutoUs = Math.Clamp(s.MaxAutoExpoUs, 1_000, MaxAutoUsCeiling);
        _updatingUi = true;
        _autoExpo.Checked = s.AutoExposure;
        _aeTargetSlider.Value = Math.Clamp(s.AeTarget, 16, 220);
        _aeTargetBox.Text = _aeTargetSlider.Value.ToString();
        int maxMs = Math.Clamp((int)(_maxAutoUs / 1000), _maxAutoSlider.Minimum, _maxAutoSlider.Maximum);
        _maxAutoSlider.Value = maxMs;
        _maxAutoBox.Text = maxMs.ToString();
        _sharpSlider.Value = Math.Clamp(s.Sharpen, 0, 500);
        _sharpBox.Text = _sharpSlider.Value.ToString();
        _denoiseSlider.Value = Math.Clamp(s.Denoise, 0, 100);
        _denoiseBox.Text = _denoiseSlider.Value.ToString();
        _smoothSlider.Value = Math.Clamp(s.Smooth, 0, 4);
        _smoothBox.Text = _smoothSlider.Value.ToString();
        _smoothLevel = _smoothSlider.Value;
        _demosaicCombo.SelectedIndex = Math.Clamp(s.DemosaicIndex, 0, 4);
        _flickerCombo.SelectedIndex = Math.Clamp(s.FlickerIndex, 0, 2);
        _gammaSlider.Value = Math.Clamp(s.Gamma, AmcamNative.GAMMA_MIN, AmcamNative.GAMMA_MAX);
        _gammaBox.Text = _gammaSlider.Value.ToString();
        _contrastSlider.Value = Math.Clamp(s.Contrast, AmcamNative.CONTRAST_MIN, AmcamNative.CONTRAST_MAX);
        _contrastBox.Text = _contrastSlider.Value.ToString();
        _satSlider.Value = Math.Clamp(s.Saturation, AmcamNative.SATURATION_MIN, AmcamNative.SATURATION_MAX);
        _satBox.Text = _satSlider.Value.ToString();
        if (s.WbTemp >= AmcamNative.TEMP_MIN && s.WbTint >= AmcamNative.TINT_MIN)
        {
            _wbTempSlider.Value = Math.Clamp(s.WbTemp, AmcamNative.TEMP_MIN, AmcamNative.TEMP_MAX);
            _wbTempBox.Text = _wbTempSlider.Value.ToString();
            _wbTintSlider.Value = Math.Clamp(s.WbTint, AmcamNative.TINT_MIN, AmcamNative.TINT_MAX);
            _wbTintBox.Text = _wbTintSlider.Value.ToString();
        }
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
            // Combo index must stay aligned with the SDK eSize index, so add
            // a placeholder even if a query fails.
            _resCombo.Items.Add(AmcamNative.Ok(AmcamNative.Amcam_get_Resolution(_cam, i, out int w, out int h))
                ? $"{w} × {h}"
                : $"mode {i}");
        }
        if (AmcamNative.Ok(AmcamNative.Amcam_get_eSize(_cam, out uint current)) && current < _resCombo.Items.Count)
            _resCombo.SelectedIndex = (int)current;
        _updatingUi = false;
    }

    private void OnResolutionChanged(object? sender, EventArgs e)
    {
        if (_updatingUi || _cam == IntPtr.Zero || _resCombo.SelectedIndex < 0)
            return;

        // Preserve the live exposure values across the restart (manual mode
        // re-applies them; auto mode re-meters anyway).
        AmcamNative.Amcam_get_ExpoTime(_cam, out uint keepExpo);
        AmcamNative.Amcam_get_ExpoAGain(_cam, out ushort keepGain);

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
        ApplyUiStateToCamera(keepExpo == 0 ? null : keepExpo, keepGain == 0 ? null : keepGain);
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
        _statusFps.Text = "";
        _frozen = false;
        _freezeButton.Text = "Freeze";
    }

    private void OnDisconnected()
    {
        CloseCamera();
        _statusCamera.Text = "Camera disconnected — waiting…";
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
        if (AmcamNative.Ok(AmcamNative.Amcam_get_AutoExpoEnable(_cam, out int auto)))
            _autoExpo.Checked = auto != 0;
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
            if (!_expoBox.Focused)
                SetExposureText(expoUs);
        }
        if (AmcamNative.Ok(AmcamNative.Amcam_get_ExpoAGain(_cam, out ushort gain)))
        {
            _gainSlider.Value = Math.Clamp(gain, (ushort)_gainSlider.Minimum, (ushort)_gainSlider.Maximum);
            if (!_gainBox.Focused)
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
            if (!_wbTempBox.Focused)
                _wbTempBox.Text = temp.ToString();
            _wbTintSlider.Value = Math.Clamp(tint, AmcamNative.TINT_MIN, AmcamNative.TINT_MAX);
            if (!_wbTintBox.Focused)
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
            _gammaBox.Text = _gammaSlider.Value.ToString();
        }
        if (AmcamNative.Ok(AmcamNative.Amcam_get_Contrast(_cam, out int contrast)))
        {
            _contrastSlider.Value = Math.Clamp(contrast, AmcamNative.CONTRAST_MIN, AmcamNative.CONTRAST_MAX);
            _contrastBox.Text = _contrastSlider.Value.ToString();
        }
        if (AmcamNative.Ok(AmcamNative.Amcam_get_Saturation(_cam, out int sat)))
        {
            _satSlider.Value = Math.Clamp(sat, AmcamNative.SATURATION_MIN, AmcamNative.SATURATION_MAX);
            _satBox.Text = _satSlider.Value.ToString();
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
                    {
                        try
                        {
                            BeginInvoke(PullAndShowFrame);
                        }
                        catch
                        {
                            // Window handle gone/being recreated — roll back
                            // so the next event can try again.
                            Interlocked.Exchange(ref _framePending, 0);
                            throw;
                        }
                    }
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
            if (_smoothLevel == 0)
            {
                if (!AmcamNative.Ok(AmcamNative.Amcam_PullImageV2(_cam, data.Scan0, 24, ref info)))
                    return;
                _smoothAccValid = false;
            }
            else
            {
                // Temporal exponential averaging: pull into a staging buffer,
                // blend into a 12.4 fixed-point accumulator, write the
                // average to the display bitmap. Strong noise reduction on
                // static scenes; moving objects ghost slightly at high levels.
                if (!PullIntoBuffer(ref info))
                    return;
                BlendSmoothed(data.Scan0, Math.Min(data.Stride * _frame.Height, _pullBuf.Length));
            }
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

    private unsafe bool PullIntoBuffer(ref AmcamNative.FrameInfoV2 info)
    {
        fixed (byte* p = _pullBuf)
        {
            return AmcamNative.Ok(AmcamNative.Amcam_PullImageV2(_cam, (IntPtr)p, 24, ref info));
        }
    }

    private unsafe void BlendSmoothed(IntPtr dstPtr, int length)
    {
        int shift = _smoothLevel; // 1..4 → blend factor 1/2 .. 1/16
        fixed (byte* src = _pullBuf)
        fixed (ushort* acc = _smoothAcc)
        {
            byte* dst = (byte*)dstPtr;
            if (!_smoothAccValid)
            {
                for (int i = 0; i < length; i++)
                {
                    acc[i] = (ushort)(src[i] << 4);
                    dst[i] = src[i];
                }
                _smoothAccValid = true;
                return;
            }
            for (int i = 0; i < length; i++)
            {
                int a = acc[i];
                a += ((src[i] << 4) - a) >> shift;
                acc[i] = (ushort)a;
                dst[i] = (byte)(a >> 4);
            }
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
        _denoiseSlider.Enabled = _denoiseBox.Enabled = enabled;
        _smoothSlider.Enabled = _smoothBox.Enabled = enabled;
        _demosaicCombo.Enabled = enabled;
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
        FlashStatus("White balance adjusted");
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
        int hz = FlickerSdkValue(_flickerCombo.SelectedIndex);
        AmcamNative.Amcam_put_HZ(_cam, hz);
        if (hz != 2 && _autoExpo.Checked)
            FlashStatus("Note: in AC mode auto exposure steps coarsely — prefer manual exposure");
    }

    private void OnFreeze(object? sender, EventArgs e)
    {
        if (_cam == IntPtr.Zero)
            return;
        _frozen = !_frozen;
        AmcamNative.Amcam_Pause(_cam, _frozen ? 1 : 0);
        _freezeButton.Text = _frozen ? "Resume" : "Freeze";
        _statusFps.Text = _frozen ? "frozen" : "";
        _fpsLabel.Text = _frozen ? "frozen" : "—";
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
        FlashStatus($"Saved {path}");
    }

    private void OnSaveDefaults(object? sender, EventArgs e)
    {
        if (_cam == IntPtr.Zero)
            return;
        var s = new AppSettings
        {
            ResolutionIndex = _resCombo.SelectedIndex,
            AutoExposure = _autoExpo.Checked,
            AeTarget = _aeTargetSlider.Value,
            MaxAutoExpoUs = _maxAutoUs,
            Sharpen = _sharpSlider.Value,
            Denoise = _denoiseSlider.Value,
            Smooth = _smoothSlider.Value,
            DemosaicIndex = _demosaicCombo.SelectedIndex,
            FlickerIndex = _flickerCombo.SelectedIndex,
            FlipH = _flipH.Checked,
            FlipV = _flipV.Checked,
            Crosshair = _crosshair.Checked,
            Gamma = _gammaSlider.Value,
            Contrast = _contrastSlider.Value,
            Saturation = _satSlider.Value,
        };
        // Only persist live camera readings when the camera confirms them —
        // a failed read would otherwise save 0 and load as a black image.
        if (AmcamNative.Ok(AmcamNative.Amcam_get_ExpoTime(_cam, out uint expoUs)) && expoUs > 0)
            s.ExposureUs = expoUs;
        if (AmcamNative.Ok(AmcamNative.Amcam_get_ExpoAGain(_cam, out ushort gain)) && gain > 0)
            s.GainPercent = gain;
        if (AmcamNative.Ok(AmcamNative.Amcam_get_TempTint(_cam, out int temp, out int tint)))
        {
            s.WbTemp = temp;
            s.WbTint = tint;
        }
        try
        {
            s.Save();
            _settings = s;
            FlashStatus("Defaults saved — applied on every start");
        }
        catch (Exception ex)
        {
            FlashStatus($"Could not save defaults: {ex.Message}");
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
