using System.Diagnostics;
using System.Drawing.Imaging;

namespace ScopeCAM;

public class MainForm : Form
{
    private const int SliderSteps = 1000;        // exposure slider resolution (log scale)
    private const uint MaxAutoExpoUs = 33_333;   // cap auto exposure at ~1/30 s for 30 fps
    private const ushort MaxAutoGainCap = 500;   // don't let auto exposure go noisier than 5x
    private const int DefaultSharpness = 150;    // moderate sharpening (range 0–500)

    private readonly PictureBox _view = new();
    private readonly CheckBox _autoExpo = new();
    private readonly TrackBar _aeTargetSlider = new();
    private readonly Label _aeTargetLabel = new();
    private readonly TrackBar _expoSlider = new();
    private readonly Label _expoLabel = new();
    private readonly TrackBar _gainSlider = new();
    private readonly Label _gainLabel = new();
    private readonly Button _wbButton = new();
    private readonly TrackBar _sharpSlider = new();
    private readonly Label _sharpLabel = new();
    private readonly ComboBox _flickerCombo = new();
    private readonly CheckBox _flipH = new();
    private readonly CheckBox _flipV = new();
    private readonly CheckBox _crosshair = new();
    private readonly Button _freezeButton = new();
    private readonly Button _snapshotButton = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusCamera = new();
    private readonly ToolStripStatusLabel _statusFps = new();
    private readonly System.Windows.Forms.Timer _reconnectTimer = new();

    private IntPtr _cam = IntPtr.Zero;
    private AmcamNative.EventCallback? _callback; // GC-rooted for the native side
    private Bitmap? _frame;
    private int _framePending; // coalesce image events so the UI thread never backlogs
    private bool _frozen;

    private uint _expoMinUs = 1, _expoMaxUs = 1;
    private ushort _gainMin = 100, _gainMax = 100;
    private bool _updatingUi; // true while controls are being set from camera values

    private readonly Stopwatch _fpsClock = Stopwatch.StartNew();
    private int _fpsFrames;

    public MainForm()
    {
        Text = "ScopeCAM";
        ClientSize = new Size(1240, 760);
        MinimumSize = new Size(760, 520);

        _view.Dock = DockStyle.Fill;
        _view.SizeMode = PictureBoxSizeMode.Zoom;
        _view.BackColor = Color.Black;
        _view.Paint += OnViewPaint;

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
            Width = 290,
            AutoScroll = true,
            Padding = new Padding(8),
            ColumnCount = 3,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));     // names
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // sliders
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62)); // readouts

        void AddFullRow(Control c)
        {
            int row = panel.RowCount++;
            panel.Controls.Add(c, 0, row);
            panel.SetColumnSpan(c, 3);
        }

        void AddSliderRow(string name, TrackBar slider, Label valueLabel)
        {
            int row = panel.RowCount++;
            slider.TickStyle = TickStyle.None;
            slider.Dock = DockStyle.Fill;
            valueLabel.Text = "—";
            valueLabel.AutoSize = true;
            valueLabel.Anchor = AnchorStyles.Left;
            panel.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            panel.Controls.Add(slider, 1, row);
            panel.Controls.Add(valueLabel, 2, row);
        }

        Label Header(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 10, 0, 2),
        };

        // --- Exposure ---
        AddFullRow(Header("Exposure"));

        _autoExpo.Text = "Auto exposure";
        _autoExpo.AutoSize = true;
        _autoExpo.CheckedChanged += OnAutoExpoChanged;
        AddFullRow(_autoExpo);

        _aeTargetSlider.Minimum = 16;
        _aeTargetSlider.Maximum = 220;
        _aeTargetSlider.ValueChanged += OnAeTargetSlider;
        AddSliderRow("Target", _aeTargetSlider, _aeTargetLabel);

        _expoSlider.Minimum = 0;
        _expoSlider.Maximum = SliderSteps;
        _expoSlider.ValueChanged += OnExposureSlider;
        AddSliderRow("Time", _expoSlider, _expoLabel);

        _gainSlider.ValueChanged += OnGainSlider;
        AddSliderRow("Gain", _gainSlider, _gainLabel);

        // --- Color & image ---
        AddFullRow(Header("Color && image"));

        _wbButton.Text = "Auto white balance";
        _wbButton.AutoSize = true;
        _wbButton.Click += OnWhiteBalance;
        AddFullRow(_wbButton);

        _sharpSlider.Minimum = 0;
        _sharpSlider.Maximum = 500;
        _sharpSlider.ValueChanged += OnSharpnessSlider;
        AddSliderRow("Sharpen", _sharpSlider, _sharpLabel);

        _flickerCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _flickerCombo.Items.AddRange(new object[] { "50 Hz lighting", "60 Hz lighting", "DC lighting" });
        _flickerCombo.SelectedIndexChanged += OnFlickerChanged;
        var flickerRow = panel.RowCount++;
        panel.Controls.Add(new Label { Text = "Flicker", AutoSize = true, Anchor = AnchorStyles.Left }, 0, flickerRow);
        panel.Controls.Add(_flickerCombo, 1, flickerRow);
        panel.SetColumnSpan(_flickerCombo, 2);
        _flickerCombo.Dock = DockStyle.Fill;

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
        _crosshair.CheckedChanged += (_, _) => _view.Invalidate();
        AddFullRow(_crosshair);

        var buttonRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        _freezeButton.Text = "Freeze";
        _freezeButton.AutoSize = true;
        _freezeButton.Click += OnFreeze;
        _snapshotButton.Text = "Snapshot";
        _snapshotButton.AutoSize = true;
        _snapshotButton.Click += OnSnapshot;
        buttonRow.Controls.Add(_freezeButton);
        buttonRow.Controls.Add(_snapshotButton);
        AddFullRow(buttonRow);

        SetConnectedControlsEnabled(false);
        return panel;
    }

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
        _cam = AmcamNative.Amcam_Open(device.id);
        if (_cam == IntPtr.Zero)
        {
            _statusCamera.Text = $"Found {device.displayname} but failed to open — retrying…";
            _reconnectTimer.Start();
            return;
        }

        if (!AmcamNative.Ok(AmcamNative.Amcam_get_Size(_cam, out int width, out int height)))
        {
            CloseCamera();
            _statusCamera.Text = "Failed to query camera resolution — retrying…";
            _reconnectTimer.Start();
            return;
        }

        ReadRanges();
        ApplyStartupDefaults();

        _frame?.Dispose();
        _frame = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        _view.Image = _frame;

        _callback = OnCameraEvent;
        if (!AmcamNative.Ok(AmcamNative.Amcam_StartPullModeWithCallback(_cam, _callback, IntPtr.Zero)))
        {
            CloseCamera();
            _statusCamera.Text = "Failed to start camera stream — retrying…";
            _reconnectTimer.Start();
            return;
        }

        // Automatic color: one-push auto white balance once frames are flowing.
        AmcamNative.Amcam_AwbOnce(_cam, IntPtr.Zero, IntPtr.Zero);

        SyncUiFromCamera();
        _reconnectTimer.Stop();
        _frozen = false;
        _freezeButton.Text = "Freeze";
        SetConnectedControlsEnabled(true);
        _statusCamera.Text = $"Connected: {device.displayname}  ({width}×{height})";
    }

    /// <summary>Startup configuration for crisp, high-framerate live view.</summary>
    private void ApplyStartupDefaults()
    {
        // Max USB bandwidth / frame speed level (not guaranteed to be the default).
        AmcamNative.Amcam_put_Speed(_cam, AmcamNative.Amcam_get_MaxSpeed(_cam));
        // Always show the newest frame; drop stale buffered frames (low latency).
        AmcamNative.Amcam_put_RealTime(_cam, 1);
        // Start in auto exposure, but cap exposure time at ~1/30 s so auto
        // exposure raises gain instead of dropping the frame rate (the SDK
        // default allows up to 350 ms ≈ 3 fps).
        AmcamNative.Amcam_put_AutoExpoEnable(_cam, 1);
        AmcamNative.Amcam_put_MaxAutoExpoTimeAGain(_cam, MaxAutoExpoUs, Math.Min(_gainMax, MaxAutoGainCap));
        // Stabilize auto exposure so it doesn't hunt on specular solder
        // reflections: meter the whole frame instead of the brightest 1%,
        // ignore small brightness changes, and damp the transitions.
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_AUTOEXPOSURE_PERCENT, 100);
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_AUTOEXP_THRESHOLD, 10);
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_AUTOEXP_EXPOTIME_DAMP, 300);
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_AUTOEXP_GAIN_DAMP, 300);
        // Anti-flicker for AC workbench lighting (default 50 Hz, selectable).
        AmcamNative.Amcam_put_HZ(_cam, 1);
        // Moderate sharpening: radius 2, threshold 0.
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_SHARPENING, (2 << 16) | DefaultSharpness);
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
            _aeTargetLabel.Text = target.ToString();
        }
        _sharpSlider.Value = DefaultSharpness;
        _sharpLabel.Text = DefaultSharpness.ToString();
        _flickerCombo.SelectedIndex = 0; // 50 Hz (matches ApplyStartupDefaults)
        _flipH.Checked = false;
        _flipV.Checked = false;
        _updatingUi = false;
        UpdateExposureControlsEnabled();
        RefreshSlidersFromCamera();
    }

    private void RefreshSlidersFromCamera()
    {
        if (_cam == IntPtr.Zero)
            return;
        _updatingUi = true;
        if (AmcamNative.Ok(AmcamNative.Amcam_get_ExpoTime(_cam, out uint expoUs)))
        {
            _expoSlider.Value = ExposureToSlider(expoUs);
            _expoLabel.Text = FormatExposure(expoUs);
        }
        if (AmcamNative.Ok(AmcamNative.Amcam_get_ExpoAGain(_cam, out ushort gain)))
        {
            _gainSlider.Value = Math.Clamp(gain, (ushort)_gainSlider.Minimum, (ushort)_gainSlider.Maximum);
            _gainLabel.Text = $"{gain} %";
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
            _statusFps.Text = $"{_fpsFrames * 1000.0 / _fpsClock.ElapsedMilliseconds:F1} fps";
            _fpsFrames = 0;
            _fpsClock.Restart();
        }
    }

    // ---------- Controls ----------

    private void SetConnectedControlsEnabled(bool enabled)
    {
        _autoExpo.Enabled = enabled;
        _wbButton.Enabled = enabled;
        _sharpSlider.Enabled = enabled;
        _flickerCombo.Enabled = enabled;
        _flipH.Enabled = enabled;
        _flipV.Enabled = enabled;
        _freezeButton.Enabled = enabled;
        _snapshotButton.Enabled = enabled || _frame is not null;
        if (enabled)
            UpdateExposureControlsEnabled();
        else
        {
            _aeTargetSlider.Enabled = false;
            _expoSlider.Enabled = false;
            _gainSlider.Enabled = false;
        }
    }

    private void UpdateExposureControlsEnabled()
    {
        bool auto = _autoExpo.Checked;
        _aeTargetSlider.Enabled = auto;
        _expoSlider.Enabled = !auto;
        _gainSlider.Enabled = !auto && _gainMax > _gainMin;
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

    private void OnAeTargetSlider(object? sender, EventArgs e)
    {
        _aeTargetLabel.Text = _aeTargetSlider.Value.ToString();
        if (_updatingUi || _cam == IntPtr.Zero)
            return;
        AmcamNative.Amcam_put_AutoExpoTarget(_cam, (ushort)_aeTargetSlider.Value);
    }

    private void OnExposureSlider(object? sender, EventArgs e)
    {
        if (_updatingUi || _cam == IntPtr.Zero)
            return;
        uint expoUs = SliderToExposure(_expoSlider.Value);
        AmcamNative.Amcam_put_ExpoTime(_cam, expoUs);
        _expoLabel.Text = FormatExposure(expoUs);
    }

    private void OnGainSlider(object? sender, EventArgs e)
    {
        if (_updatingUi || _cam == IntPtr.Zero)
            return;
        AmcamNative.Amcam_put_ExpoAGain(_cam, (ushort)_gainSlider.Value);
        _gainLabel.Text = $"{_gainSlider.Value} %";
    }

    private void OnWhiteBalance(object? sender, EventArgs e)
    {
        if (_cam == IntPtr.Zero)
            return;
        AmcamNative.Amcam_AwbOnce(_cam, IntPtr.Zero, IntPtr.Zero);
        _statusCamera.Text = "White balance adjusted";
    }

    private void OnSharpnessSlider(object? sender, EventArgs e)
    {
        int strength = _sharpSlider.Value;
        _sharpLabel.Text = strength == 0 ? "off" : strength.ToString();
        if (_updatingUi || _cam == IntPtr.Zero)
            return;
        AmcamNative.Amcam_put_Option(_cam, AmcamNative.OPTION_SHARPENING, (2 << 16) | strength);
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

    private void OnViewPaint(object? sender, PaintEventArgs e)
    {
        if (!_crosshair.Checked)
            return;
        // PictureBox Zoom mode centers the image, so the control center is
        // the image center.
        int cx = _view.Width / 2, cy = _view.Height / 2;
        using var pen = new Pen(Color.FromArgb(190, Color.Red));
        e.Graphics.DrawLine(pen, cx, 0, cx, _view.Height);
        e.Graphics.DrawLine(pen, 0, cy, _view.Width, cy);
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

    private static string FormatExposure(uint expoUs) =>
        expoUs >= 1000 ? $"{expoUs / 1000.0:F1} ms" : $"{expoUs} µs";
}
