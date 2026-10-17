using System.Drawing.Drawing2D;

namespace ScopeCAM;

/// <summary>
/// Fast live-video surface. PictureBox scales with high-quality bicubic
/// interpolation, which costs tens of milliseconds per 5 MP frame on the UI
/// thread; this control draws with fast bilinear scaling and no compositing
/// overhead, keeping display latency low at full resolution.
/// </summary>
public sealed class VideoView : Control
{
    public Bitmap? Frame;
    public bool Crosshair;

    public VideoView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
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

            // Aspect-fit, centered (same geometry as PictureBox Zoom mode).
            double scale = Math.Min((double)Width / Frame.Width, (double)Height / Frame.Height);
            int w = (int)(Frame.Width * scale), h = (int)(Frame.Height * scale);
            g.DrawImage(Frame, (Width - w) / 2, (Height - h) / 2, w, h);
        }
        if (Crosshair)
        {
            int cx = Width / 2, cy = Height / 2;
            using var pen = new Pen(Color.FromArgb(190, Color.Red));
            g.DrawLine(pen, cx, 0, cx, Height);
            g.DrawLine(pen, 0, cy, Width, cy);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }
}
