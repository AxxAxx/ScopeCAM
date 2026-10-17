using System.Drawing.Drawing2D;

namespace ScopeCAM;

/// <summary>
/// Fast live-video surface with mouse-wheel zoom and drag-to-pan. PictureBox
/// scales with high-quality bicubic interpolation (tens of ms per 5 MP frame
/// on the UI thread); this draws with fast bilinear scaling and no
/// compositing overhead, keeping display latency low at full resolution.
/// </summary>
public sealed class VideoView : Control
{
    public Bitmap? Frame;
    public bool Crosshair;

    private const float MaxZoom = 20f;
    private float _zoom = 1f;
    private float _panX, _panY;       // image-center offset from control center, px
    private bool _dragging;
    private Point _dragStart;
    private float _panStartX, _panStartY;

    public VideoView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque |
                 ControlStyles.Selectable, true);
        TabStop = false;
        MouseEnter += (_, _) => Focus();       // so the wheel reaches us
        MouseWheel += OnWheel;
        MouseDown += OnDown;
        MouseMove += OnMove;
        MouseUp += OnUp;
        DoubleClick += (_, _) => ResetZoom();
    }

    public void ResetZoom()
    {
        _zoom = 1f;
        _panX = _panY = 0f;
        Invalidate();
    }

    private float BaseScale() =>
        Frame is null ? 1f : Math.Min((float)Width / Frame.Width, (float)Height / Frame.Height);

    private void OnWheel(object? sender, MouseEventArgs e)
    {
        if (Frame is null)
            return;
        float nz = Math.Clamp(_zoom * (e.Delta > 0 ? 1.2f : 1f / 1.2f), 1f, MaxZoom);
        if (nz == _zoom)
            return;

        // Keep the image point under the cursor fixed while zooming.
        float s0 = BaseScale();
        float sOld = s0 * _zoom, sNew = s0 * nz;
        float dxOld = Width / 2f + _panX - Frame.Width * sOld / 2f;
        float dyOld = Height / 2f + _panY - Frame.Height * sOld / 2f;
        float ix = (e.X - dxOld) / sOld;
        float iy = (e.Y - dyOld) / sOld;

        _zoom = nz;
        if (_zoom <= 1f)
        {
            _panX = _panY = 0f; // snap back to centered fit
        }
        else
        {
            float cxNew = e.X - ix * sNew + Frame.Width * sNew / 2f;
            float cyNew = e.Y - iy * sNew + Frame.Height * sNew / 2f;
            _panX = cxNew - Width / 2f;
            _panY = cyNew - Height / 2f;
            ClampPan();
        }
        Invalidate();
    }

    private void OnDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _zoom > 1f)
        {
            _dragging = true;
            _dragStart = e.Location;
            _panStartX = _panX;
            _panStartY = _panY;
            Cursor = Cursors.SizeAll;
        }
    }

    private void OnMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;
        _panX = _panStartX + (e.X - _dragStart.X);
        _panY = _panStartY + (e.Y - _dragStart.Y);
        ClampPan();
        Invalidate();
    }

    private void OnUp(object? sender, MouseEventArgs e)
    {
        _dragging = false;
        Cursor = Cursors.Default;
    }

    // Keep at least part of the image on screen (don't let it fly away).
    private void ClampPan()
    {
        if (Frame is null)
            return;
        float s = BaseScale() * _zoom;
        float halfW = Frame.Width * s / 2f, halfH = Frame.Height * s / 2f;
        float limX = Math.Max(0, halfW), limY = Math.Max(0, halfH);
        _panX = Math.Clamp(_panX, -limX, limX);
        _panY = Math.Clamp(_panY, -limY, limY);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);
        if (Frame is not null)
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.SmoothingMode = SmoothingMode.None;

            float s = BaseScale() * _zoom;
            float dw = Frame.Width * s, dh = Frame.Height * s;
            float dx = Width / 2f + _panX - dw / 2f;
            float dy = Height / 2f + _panY - dh / 2f;
            g.DrawImage(Frame, dx, dy, dw, dh);
        }
        if (Crosshair)
        {
            int cx = Width / 2, cy = Height / 2;
            using var pen = new Pen(Color.FromArgb(190, Color.Red));
            g.DrawLine(pen, cx, 0, cx, Height);
            g.DrawLine(pen, 0, cy, Width, cy);
        }
        if (_zoom > 1f)
        {
            using var font = new Font(FontFamily.GenericSansSerif, 9f);
            using var bg = new SolidBrush(Color.FromArgb(140, Color.Black));
            string t = $"{_zoom:0.0}×  (double-click to reset)";
            var sz = g.MeasureString(t, font);
            g.FillRectangle(bg, 4, 4, sz.Width + 8, sz.Height + 4);
            g.DrawString(t, font, Brushes.White, 8, 6);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }
}
