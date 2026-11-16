using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OpenCodeTrafficLight
{
    /// <summary>
    /// 无边框、真透明、始终置顶的交通灯悬浮窗。
    /// 绘制逻辑逐项对应 macOS 版 TrafficLightView：同样的 52×127 比例、
    /// 同样的胶囊灯箱、径向渐变灯珠、外发光与高光。
    /// </summary>
    internal sealed class TrafficLightWindow : Form
    {
        // 与 AppKit 版一致的配色
        private static readonly Color HousingFill = Color.FromArgb(255, 22, 24, 26);
        private static readonly Color HousingStroke = Color.FromArgb(17, 255, 255, 255);
        private static readonly Color LampDark = Color.FromArgb(255, 30, 32, 34);
        private static readonly Color LampDarkStroke = Color.FromArgb(10, 255, 255, 255);

        private static readonly Color LampRed = Color.FromArgb(255, 235, 74, 61);
        private static readonly Color LampYellow = Color.FromArgb(255, 250, 194, 74);
        private static readonly Color LampGreen = Color.FromArgb(255, 64, 204, 107);

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private readonly ToolTip _toolTip = new ToolTip { InitialDelay = 350, ReshowDelay = 120 };
        private readonly Timer _animTimer;
        private readonly ContextMenuStrip _menu;

        private LightState _state = new LightState();
        private float _scale = 1f;
        private bool _dragging;
        private bool _moved;
        private Point _dragStartCursor;
        private Point _dragStartLocation;

        public event EventHandler RefreshRequested;
        public event EventHandler OpenRequested;
        public event EventHandler HideRequested;
        public event EventHandler QuitRequested;
        public event EventHandler ResetPositionRequested;
        public event EventHandler PositionChanged;

        public TrafficLightWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Black;
            TopMost = true;
            Text = "OpenCode 红绿灯";

            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);

            _animTimer = new Timer { Interval = 33 };
            _animTimer.Tick += (s, e) => RenderFrame();

            _menu = BuildMenu();
            ContextMenuStrip = _menu;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        // ------------------------------------------------------------- 生命周期

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDpi();
            RenderFrame();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            ApplyDpi();
            RenderFrame();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 分层窗口隐藏/显示后需要重新上屏
            RenderFrame();
            UpdateAnimationTimer();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                RenderFrame();
                UpdateAnimationTimer();
            }
            else
            {
                _animTimer.Stop();
            }
        }

        private void ApplyDpi()
        {
            try { _scale = DeviceDpi / 96f; }
            catch (Exception) { _scale = 1f; }
            if (_scale <= 0.1f) _scale = 1f;

            int width = (int)Math.Round(Metrics.WindowWidth * _scale);
            int height = (int)Math.Round(Metrics.WindowHeight * _scale);
            ClientSize = new Size(width, height);
        }

        public Size LogicalToPixels()
        {
            return new Size(
                (int)Math.Round(Metrics.WindowWidth * _scale),
                (int)Math.Round(Metrics.WindowHeight * _scale));
        }

        // ---------------------------------------------------------------- 状态

        public LightState State
        {
            get { return _state; }
        }

        public void Apply(LightState state)
        {
            bool lampChanged = !state.SameLampAs(_state);
            _state = state;

            if (IsHandleCreated)
            {
                _toolTip.SetToolTip(this, state.TooltipText);
            }

            if (lampChanged || !_animTimer.Enabled)
            {
                RenderFrame();
            }
            UpdateAnimationTimer();
        }

        private void UpdateAnimationTimer()
        {
            bool blinking = Visible && _state.Blink != BlinkKind.None;
            if (blinking && !_animTimer.Enabled) _animTimer.Start();
            else if (!blinking && _animTimer.Enabled) _animTimer.Stop();
        }

        // ---------------------------------------------------------------- 绘制

        private void RenderFrame()
        {
            if (!IsHandleCreated) return;

            int width = Math.Max(1, (int)Math.Round(Metrics.WindowWidth * _scale));
            int height = Math.Max(1, (int)Math.Round(Metrics.WindowHeight * _scale));

            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;

                DrawHousing(g, width, height);
                DrawLamps(g, width, height);

                PushLayered(bitmap);
            }
        }

        private void DrawHousing(Graphics g, int width, int height)
        {
            float inset = Metrics.HousingInset * _scale;
            var rect = new RectangleF(inset, inset, width - inset * 2f, height - inset * 2f);
            using (var path = RoundedRect(rect, rect.Width / 2f))
            using (var fill = new SolidBrush(HousingFill))
            using (var pen = new Pen(HousingStroke, Math.Max(1f, 0.8f * _scale)))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
        }

        private void DrawLamps(Graphics g, int width, int height)
        {
            float inset = Metrics.HousingInset * _scale;
            float diameter = Metrics.LampDiameter * _scale;
            float gap = Metrics.LampGap * _scale;
            float housingHeight = height - inset * 2f;
            float total = diameter * 3f + gap * 2f;
            float top = inset + (housingHeight - total) / 2f;
            float left = width / 2f - diameter / 2f;

            int litIndex = (int)_state.Mode; // Red=0, Yellow=1, Green=2
            float opacity = LampOpacity();

            for (int index = 0; index < 3; index++)
            {
                var rect = new RectangleF(left, top + index * (diameter + gap), diameter, diameter);
                bool lit = index == litIndex;
                DrawLamp(g, rect, LampColor(index), lit, lit ? opacity : 1f);
            }
        }

        private void DrawLamp(Graphics g, RectangleF rect, Color baseColor, bool lit, float opacity)
        {
            if (!lit)
            {
                using (var fill = new SolidBrush(LampDark))
                using (var pen = new Pen(LampDarkStroke, Math.Max(1f, 0.8f * _scale)))
                {
                    g.FillEllipse(fill, rect);
                    g.DrawEllipse(pen, rect);
                }
                return;
            }

            if (opacity <= 0.01f) return;

            float glowPad = 3f * _scale;

            // 外发光
            using (var glow = new SolidBrush(Color.FromArgb((int)(51 * opacity), baseColor)))
            {
                g.FillEllipse(glow, RectangleF.Inflate(rect, glowPad, glowPad));
            }

            // 主体：径向渐变，中心偏上
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(rect);
                using (var brush = new PathGradientBrush(path))
                {
                    brush.CenterColor = WithAlpha(Blend(baseColor, Color.White, 0.42f), opacity);
                    brush.SurroundColors = new[] { WithAlpha(Blend(baseColor, Color.Black, 0.30f), opacity) };
                    float radius = rect.Width / 2f;
                    brush.CenterPoint = new PointF(
                        rect.X + rect.Width / 2f - radius * 0.12f,
                        rect.Y + rect.Height / 2f - radius * 0.28f);
                    g.FillEllipse(brush, rect);
                }
            }

            // 高光（小而柔）
            var highlight = RectangleF.Inflate(rect, -rect.Width * 0.34f, -rect.Height * 0.36f);
            highlight.Offset(-rect.Width * 0.02f, -rect.Height * 0.12f);
            using (var brush = new SolidBrush(WithAlpha(Color.White, opacity * 0.16f)))
            {
                g.FillEllipse(brush, highlight);
            }

            // 边缘压暗
            using (var pen = new Pen(WithAlpha(Blend(baseColor, Color.Black, 0.32f), opacity * 0.5f), 1f))
            {
                g.DrawEllipse(pen, rect);
            }
        }

        private float LampOpacity()
        {
            double seconds = Clock.Elapsed.TotalSeconds;

            switch (_state.Blink)
            {
                case BlinkKind.YellowPulse:
                {
                    double phase = (seconds % 1.6) / 1.6;
                    float k = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * phase));
                    return 0.34f + 0.66f * k;
                }
                case BlinkKind.GreenSoft:
                {
                    double phase = (seconds % 1.0) / 1.0;
                    float k = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * phase));
                    return 0.52f + 0.48f * k;
                }
                case BlinkKind.RedFast:
                {
                    // 2 Hz：亮 0.3s / 灭 0.2s
                    double phase = (seconds % 0.5) / 0.5;
                    return phase < 0.6 ? 1f : 0.18f;
                }
                default:
                    return 1f;
            }
        }

        private static Color LampColor(int index)
        {
            if (index == 0) return LampRed;
            if (index == 1) return LampYellow;
            return LampGreen;
        }

        private static Color Blend(Color a, Color b, float amount)
        {
            return Color.FromArgb(
                a.A,
                (int)Math.Round(a.R + (b.R - a.R) * amount),
                (int)Math.Round(a.G + (b.G - a.G) * amount),
                (int)Math.Round(a.B + (b.B - a.B) * amount));
        }

        private static Color WithAlpha(Color color, float alpha)
        {
            int a = (int)Math.Round(255 * Math.Max(0f, Math.Min(1f, alpha)) * (color.A / 255f));
            return Color.FromArgb(Math.Max(0, Math.Min(255, a)), color.R, color.G, color.B);
        }

        private static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2f;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void PushLayered(Bitmap bitmap)
        {
            IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
            IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;

            try
            {
                hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                oldBitmap = NativeMethods.SelectObject(memDc, hBitmap);

                var size = new NativeMethods.SIZE(bitmap.Width, bitmap.Height);
                var sourcePoint = new NativeMethods.POINT(0, 0);
                var destPoint = new NativeMethods.POINT(Left, Top);
                var blend = new NativeMethods.BLENDFUNCTION
                {
                    BlendOp = NativeMethods.AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = NativeMethods.AC_SRC_ALPHA,
                };

                NativeMethods.UpdateLayeredWindow(Handle, screenDc, ref destPoint, ref size,
                    memDc, ref sourcePoint, 0, ref blend, NativeMethods.ULW_ALPHA);
            }
            finally
            {
                if (hBitmap != IntPtr.Zero)
                {
                    NativeMethods.SelectObject(memDc, oldBitmap);
                    NativeMethods.DeleteObject(hBitmap);
                }
                NativeMethods.DeleteDC(memDc);
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // ---------------------------------------------------------------- 交互

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            _dragging = true;
            _moved = false;
            _dragStartCursor = Cursor.Position;
            _dragStartLocation = Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;

            var now = Cursor.Position;
            int dx = now.X - _dragStartCursor.X;
            int dy = now.Y - _dragStartCursor.Y;

            if (Math.Abs(dx) + Math.Abs(dy) > 2) _moved = true;
            Location = new Point(_dragStartLocation.X + dx, _dragStartLocation.Y + dy);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_dragging) return;

            _dragging = false;
            if (_moved) PositionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void MoveTo(Point location)
        {
            Location = location;
            if (IsHandleCreated) RenderFrame();
        }

        // ---------------------------------------------------------------- 菜单

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip { ShowImageMargin = false };

            var refresh = new ToolStripMenuItem("立即刷新");
            refresh.Click += (s, e) => RefreshRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(refresh);

            var open = new ToolStripMenuItem("打开 OpenCode");
            open.Click += (s, e) => OpenRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(open);

            menu.Items.Add(new ToolStripSeparator());

            var pin = new ToolStripMenuItem("始终置顶") { CheckOnClick = false, Checked = TopMost };
            pin.Click += (s, e) =>
            {
                TopMost = !TopMost;
                pin.Checked = TopMost;
                if (IsHandleCreated)
                {
                    NativeMethods.SetWindowPos(Handle, TopMost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
                        0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                }
            };
            menu.Items.Add(pin);

            var reset = new ToolStripMenuItem("复位位置");
            reset.Click += (s, e) => ResetPositionRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(reset);

            menu.Items.Add(new ToolStripSeparator());

            var hide = new ToolStripMenuItem("隐藏（OpenCode 重启后恢复）");
            hide.Click += (s, e) => HideRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(hide);

            var quit = new ToolStripMenuItem("退出");
            quit.Click += (s, e) => QuitRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(quit);

            return menu;
        }

        // ---------------------------------------------------------------- 托盘图标

        public static Icon CreateTrayIcon()
        {
            using (var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                using (var body = new SolidBrush(Color.FromArgb(255, 26, 28, 30)))
                using (var path = RoundedRect(new RectangleF(7f, 2f, 18f, 28f), 9f))
                {
                    g.FillPath(body, path);
                }

                DrawTrayLamp(g, 11f, 5f, LampRed, true);
                DrawTrayLamp(g, 11f, 12.5f, LampYellow, true);
                DrawTrayLamp(g, 11f, 20f, LampGreen, true);

                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (var icon = Icon.FromHandle(handle))
                    {
                        return (Icon)icon.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        private static void DrawTrayLamp(Graphics g, float x, float y, Color color, bool lit)
        {
            var rect = new RectangleF(x, y, 10f, 10f);
            using (var fill = new SolidBrush(lit ? color : LampDark))
            {
                g.FillEllipse(fill, rect);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }
}
