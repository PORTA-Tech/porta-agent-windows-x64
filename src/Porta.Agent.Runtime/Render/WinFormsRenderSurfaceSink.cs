using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Porta.Agent.Common.Control;
using Porta.Agent.Common.Environment;

namespace Porta.Agent.Runtime.Render;

internal sealed class WinFormsRenderSurfaceSink : IRenderSurfaceSink
{
    private readonly Thread _uiThread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly string _title;
    private RenderWindow? _window;
    private bool _disposed;

    public WinFormsRenderSurfaceSink(string title = "PORTA Adaptive Surface")
    {
        _title = title;
        _uiThread = new Thread(UiMain)
        {
            IsBackground = true,
            Name = "porta-render-surface-ui"
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public void Draw(RenderSurfaceFrame frame)
    {
        RenderWindow? window = Volatile.Read(ref _window);
        if (window == null || window.IsDisposed)
        {
            return;
        }

        window.PublishFrame(frame);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RenderWindow? window = Volatile.Read(ref _window);
        if (window != null && !window.IsDisposed)
        {
            try
            {
                window.BeginInvoke(new Action(window.Close));
            }
            catch
            {
            }
        }
    }

    private void UiMain()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var window = new RenderWindow(_title);
        _window = window;
        _ready.Set();
        Application.Run(window);
        _window = null;
    }

    private sealed class RenderWindow : Form
    {
        private readonly object _sync = new();
        private RenderSurfaceFrame _frame;

        public RenderWindow(string title)
        {
            Text = title;
            StartPosition = FormStartPosition.Manual;
            Bounds = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(60, 60, 1280, 720);
            TopMost = true;
            DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = true;
            KeyPreview = true;
            BackColor = Color.Black;
            Opacity = 0.94;
            _frame = new RenderSurfaceFrame(
                RenderTimestamp: 0,
                Background: new BackgroundGradientLayer(new ColorRgb(20, 20, 24), new ColorRgb(10, 10, 14), 1.0, new ColorRgb(220, 220, 230), new ColorRgb(80, 86, 96), 1.0, 0),
                Motion: new MotionFieldLayer(20, 0.2, 0.02, 0.25),
                Depth: new DepthLayer(0.002, 0.1, 1.0),
                Overlay: new OverlayStateLayer("SAFE", AuthorityLevel.Safe, true, 0.2, 400));
            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    Close();
                }
            };
        }

        public void PublishFrame(RenderSurfaceFrame frame)
        {
            if (IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action<RenderSurfaceFrame>(PublishFrame), frame);
                }
                catch
                {
                }
                return;
            }

            lock (_sync)
            {
                _frame = frame;
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            RenderSurfaceFrame frame;
            lock (_sync)
            {
                frame = _frame;
            }

            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle rect = ClientRectangle;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return;
            }

            DrawBackground(g, rect, frame);
            DrawMotion(g, rect, frame);
            DrawDepth(g, rect, frame);
            DrawOverlay(g, rect, frame);
        }

        private static void DrawBackground(Graphics g, Rectangle rect, RenderSurfaceFrame frame)
        {
            Color top = ToColor(frame.Background.TopColor);
            Color bottom = ToColor(frame.Background.BottomColor);
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Point(0, 0),
                new Point(0, rect.Height),
                top,
                bottom);
            g.FillRectangle(brush, rect);
        }

        private static void DrawMotion(Graphics g, Rectangle rect, RenderSurfaceFrame frame)
        {
            int particles = Math.Clamp(frame.Motion.ParticleDensity, 8, 240);
            double t = frame.RenderTimestamp / 10_000_000.0;
            double vel = Math.Max(0.05, frame.Motion.MotionVelocity);
            double amp = Math.Max(0.001, frame.Motion.NoiseAmplitude);
            double cadence = Math.Max(0.05, frame.Motion.LoopCadenceHz);
            for (int i = 0; i < particles; i++)
            {
                double phase = (t * vel * (1.0 + (0.1 * cadence))) + (i * 0.6180339887);
                double x = Frac(phase * 0.73) * rect.Width;
                double yBase = Frac(phase * 1.37) * rect.Height;
                double y = yBase + (Math.Sin((phase * 6.2831853071) + (i * 0.2)) * amp * rect.Height * 0.2);
                int size = (int)Math.Round(Math.Clamp(1.0 + (amp * 50.0), 1.0, 6.0), MidpointRounding.AwayFromZero);
                int alpha = (int)Math.Round(Math.Clamp(70 + (frame.Depth.Clarity * 30), 30, 160), MidpointRounding.AwayFromZero);
                using var b = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
                g.FillEllipse(b, (float)x, (float)y, size, size);
            }
        }

        private static void DrawDepth(Graphics g, Rectangle rect, RenderSurfaceFrame frame)
        {
            double t = frame.RenderTimestamp / 10_000_000.0;
            float cx = rect.Width * 0.5f;
            float cy = rect.Height * 0.5f;
            float px = (float)(Math.Sin(t * 0.9) * frame.Depth.ParallaxAmount * rect.Width * 3.0);
            float py = (float)(Math.Cos(t * 0.7) * frame.Depth.ParallaxAmount * rect.Height * 3.0);
            RectangleF vignetteRect = new(
                px - rect.Width * 0.3f,
                py - rect.Height * 0.3f,
                rect.Width * 1.6f,
                rect.Height * 1.6f);
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddEllipse(vignetteRect);
            using var brush = new System.Drawing.Drawing2D.PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(0, 0, 0, 0),
                SurroundColors = [Color.FromArgb((int)Math.Round(Math.Clamp(frame.Depth.VignetteStrength * 255.0, 0, 170), MidpointRounding.AwayFromZero), 0, 0, 0)]
            };
            g.FillRectangle(brush, rect);
            _ = cx;
            _ = cy;
        }

        private static void DrawOverlay(Graphics g, Rectangle rect, RenderSurfaceFrame frame)
        {
            string text =
                $"STATE {frame.Overlay.BadgeText}  transition={frame.Overlay.TransitionMs:F0}ms  " +
                $"velocity={frame.Motion.MotionVelocity:F2}  kelvin={frame.Background.KelvinShift:F0}";
            using var badgeBrush = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
            using var textBrush = new SolidBrush(Color.White);
            using var font = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Point);
            SizeF size = g.MeasureString(text, font);
            RectangleF box = new(16, 16, size.Width + 20, size.Height + 12);
            g.FillRectangle(badgeBrush, box);
            g.DrawString(text, font, textBrush, 26, 22);
        }

        private static double Frac(double x)
        {
            return x - Math.Floor(x);
        }

        private static Color ToColor(in ColorRgb c)
        {
            return Color.FromArgb(c.R, c.G, c.B);
        }
    }
}
