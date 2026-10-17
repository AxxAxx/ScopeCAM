using System.Diagnostics;
using System.Drawing.Imaging;

namespace ScopeCAM;

public class MainForm : Form
{
    private const int SliderSteps = 1000; // exposure slider resolution (log scale)

    private readonly PictureBox _view = new();
    private readonly CheckBox _autoExpo = new();
    private readonly TrackBar _expoSlider = new();
    private readonly Label _expoLabel = new();
    private readonly TrackBar _gainSlider = new();
    private readonly Label _gainLabel = new();
    private readonly Button _snapshotButton = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusCamera = new();
    private readonly ToolStripStatusLabel _statusFps = new();
    private readonly System.Windows.Forms.Timer _reconnectTimer = new();

    private IntPtr _cam = IntPtr.Zero;
    private AmcamNative.EventCallback? _callback; // GC-rooted for the native side
    private Bitmap? _frame;
    private int _framePending; // coalesce image events so the UI thread never backlogs

    private uint _expoMinUs = 1, _expoMaxUs = 1;
    private ushort _gainMin = 100, _gainMax = 100;
    private bool _updatingUi; // true while sliders are being set from camera values

    private readonly Stopwatch _fpsClock = Stopwatch.StartNew();
    private int _fpsFrames;

    public MainForm()
    {
        Text = "ScopeCAM";
        ClientSize = new Size(1000, 760);
        MinimumSize = new Size(560, 440);

        _view.Dock = DockStyle.Fill;
        _view.SizeMode = PictureBoxSizeMode.Zoom;
        _view.BackColor = Color.Black;

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
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(8, 4, 8, 4),
            ColumnCount = 4,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));            // labels
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));        // sliders
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));        // value readouts
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));            // buttons

        _autoExpo.Text = "Auto exposure";
        _autoExpo.AutoSize = true;
        _autoExpo.Anchor = AnchorStyles.Left;
        _autoExpo.CheckedChanged += OnAutoExpoChanged;

        _snapshotButton.Text = "Snapshot";
        _snapshotButton.AutoSize = true;
        _snapshotButton.Enabled = false;
        _snapshotButton.Click += OnSnapshot;

        _expoSlider.Minimum = 0;
        _expoSlider.Maximum = SliderSteps;
        _expoSlider.TickStyle = TickStyle.None;
        _expoSlider.Dock = DockStyle.Fill;
        _expoSlider.ValueChanged += OnExposureSlider;

        _gainSlider.TickStyle = TickStyle.None;
        _gainSlider.Dock = DockStyle.Fill;
        _gainSlider.ValueChanged += OnGainSlider;

        _expoLabel.Text = "—";
        _expoLabel.Anchor = AnchorStyles.Left;
        _expoLabel.AutoSize = true;
        _gainLabel.Text = "—";
        _gainLabel.Anchor = AnchorStyles.Left;
        _gainLabel.AutoSize = true;

        panel.Controls.Add(_autoExpo, 0, 0);
        panel.SetColumnSpan(_autoExpo, 3);
        panel.Controls.Add(_snapshotButton, 3, 0);
        panel.SetRowSpan(_snapshotButton, 3);

        panel.Controls.Add(new Label { Text = "Exposure", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        panel.Controls.Add(_expoSlider, 1, 1);
        panel.Controls.Add(_expoLabel, 2, 1);

        panel.Controls.Add(new Label { Text = "Gain", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        panel.Controls.Add(_gainSlider, 1, 2);
        panel.Controls.Add(_gainLabel, 2, 2);

        SetSlidersEnabled(false);
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

        _frame?.Dispose();
        _frame = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        _view.Image = _frame;

        ReadRangesAndValues();

        _callback = OnCameraEvent;
        if (!AmcamNative.Ok(AmcamNative.Amcam_StartPullModeWithCallback(_cam, _callback, IntPtr.Zero)))
        {
            CloseCamera();
            _statusCamera.Text = "Failed to start camera stream — retrying…";
            _reconnectTimer.Start();
            return;
        }

        _reconnectTimer.Stop();
        _snapshotButton.Enabled = true;
        _statusCamera.Text = $"Connected: {device.displayname}  ({width}×{height})";
    }

    private void CloseCamera()
    {
        if (_cam == IntPtr.Zero)
            return;
        var cam = _cam;
        _cam = IntPtr.Zero;
        AmcamNative.Amcam_Close(cam);
        _callback = null;
        _snapshotButton.Enabled = false;
        SetSlidersEnabled(false);
    }

    private void OnDisconnected()
    {
        CloseCamera();
        _statusCamera.Text = "Camera disconnected — waiting…";
        _statusFps.Text = "";
        _reconnectTimer.Start();
    }

    private void ReadRangesAndValues()
    {
        AmcamNative.Amcam_get_ExpTimeRange(_cam, out _expoMinUs, out _expoMaxUs, out _);
        if (_expoMinUs < 1) _expoMinUs = 1;
        if (_expoMaxUs <= _expoMinUs) _expoMaxUs = _expoMinUs + 1;

        AmcamNative.Amcam_get_ExpoAGainRange(_cam, out _gainMin, out _gainMax, out _);
        _updatingUi = true;
        _gainSlider.Minimum = _gainMin;
        _gainSlider.Maximum = Math.Max(_gainMax, (ushort)(_gainMin + 1));
        _updatingUi = false;

        AmcamNative.Amcam_get_AutoExpoEnable(_cam, out int auto);
        _updatingUi = true;
        _autoExpo.Checked = auto != 0;
        _updatingUi = false;
        SetSlidersEnabled(auto == 0);

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

    private void SetSlidersEnabled(bool enabled)
    {
        _expoSlider.Enabled = enabled;
        _gainSlider.Enabled = enabled && _gainMax > _gainMin;
    }

    private void OnAutoExpoChanged(object? sender, EventArgs e)
    {
        if (_updatingUi || _cam == IntPtr.Zero)
            return;
        AmcamNative.Amcam_put_AutoExpoEnable(_cam, _autoExpo.Checked ? 1 : 0);
        SetSlidersEnabled(!_autoExpo.Checked);
        if (!_autoExpo.Checked)
            RefreshSlidersFromCamera(); // sliders take over from the last auto values
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
