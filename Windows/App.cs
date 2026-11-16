using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OpenCodeTrafficLight
{
    /// <summary>组合根：窗口 + 托盘 + 轮询 + 进程守护 + 自检。</summary>
    internal sealed class TrafficLightApp : ApplicationContext
    {
        private readonly TrafficLightWindow _window;
        private readonly NotifyIcon _tray;
        private readonly Timer _pollTimer;
        private readonly Timer _watchTimer;
        private readonly Settings _settings;
        private readonly bool _selfTest;
        private readonly string _fixedState;

        private bool _pollBusy;
        private bool _disposed;
        private bool _positionReady;
        private DateTime _forceShowUntil = DateTime.MinValue;
        private int _selfTestIndex;
        private Timer _selfTestTimer;

        public TrafficLightApp(bool selfTest, string fixedState)
        {
            _selfTest = selfTest;
            _fixedState = fixedState;
            _settings = Settings.Load();

            _window = new TrafficLightWindow { TopMost = _settings.TopMost };
            _window.RefreshRequested += (s, e) => PollTick();
            _window.OpenRequested += (s, e) => OpenOpenCode();
            _window.HideRequested += (s, e) => HideTemporarily();
            _window.QuitRequested += (s, e) => Quit();
            _window.ResetPositionRequested += (s, e) =>
            {
                _window.MoveTo(DefaultLocation());
                PersistLocation();
            };
            _window.PositionChanged += (s, e) => PersistLocation();

            _tray = new NotifyIcon
            {
                Icon = TrafficLightWindow.CreateTrayIcon(),
                Visible = true,
                Text = "OpenCode 红绿灯",
                ContextMenuStrip = BuildTrayMenu(),
            };
            _tray.DoubleClick += (s, e) => ShowWindow();

            _pollTimer = new Timer { Interval = (int)(Metrics.PollIntervalSeconds * 1000) };
            _pollTimer.Tick += (s, e) => PollTick();

            _watchTimer = new Timer { Interval = (int)(Metrics.WatchIntervalSeconds * 1000) };
            _watchTimer.Tick += (s, e) => WatchTick();

            if (_selfTest)
            {
                ShowWindow();
                StartSelfTest();
            }
            else
            {
                WatchTick();
                _pollTimer.Start();
                _watchTimer.Start();
            }
        }

        // ------------------------------------------------------------- 位置

        private Point InitialLocation()
        {
            var size = _window.LogicalToPixels();

            if (_settings.X != int.MinValue && _settings.Y != int.MinValue)
            {
                var saved = new Rectangle(new Point(_settings.X, _settings.Y), size);
                bool visible = Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(saved));
                if (visible) return saved.Location;
            }
            return DefaultLocation();
        }

        private Point DefaultLocation()
        {
            var size = _window.LogicalToPixels();
            var area = Screen.PrimaryScreen.WorkingArea;
            return new Point(area.Right - size.Width - 16, area.Top + 16);
        }

        private void PersistLocation()
        {
            _settings.X = _window.Location.X;
            _settings.Y = _window.Location.Y;
            _settings.TopMost = _window.TopMost;
            _settings.Save();
        }

        // ------------------------------------------------------------- 轮询

        private void PollTick()
        {
            if (_pollBusy) return;

            // 窗口隐藏时不查询（托盘点「显示」后会立即补一次）
            if (!_selfTest && !_window.Visible) return;

            _pollBusy = true;

            Task.Run(() => Monitor.Snapshot()).ContinueWith(task =>
            {
                var state = task.Status == TaskStatus.RanToCompletion
                    ? task.Result
                    : new LightState(LightMode.Red, BlinkKind.None, "无法读取 OpenCode 状态", "状态查询失败。");

                Action apply = () =>
                {
                    _pollBusy = false;
                    if (_disposed) return;
                    _window.Apply(state);
                };

                if (_window.IsHandleCreated && _window.InvokeRequired)
                    _window.BeginInvoke(apply);
                else
                    apply();
            });
        }

        // ------------------------------------------------------------- 进程守护

        private void WatchTick()
        {
            bool running = OpenCodeProcess.IsRunning();

            if (running)
            {
                if (!Settings.IsHiddenPauseActive() && !_window.Visible) ShowWindow();
            }
            else
            {
                Settings.ClearHidden();

                // 用户刚从托盘点了「显示」：给 30 秒宽限，避免立刻被隐藏
                if (DateTime.UtcNow < _forceShowUntil) return;

                if (_window.Visible) _window.Hide();
            }
        }

        // ------------------------------------------------------------- 自检

        private void StartSelfTest()
        {
            var states = new[]
            {
                new LightState(LightMode.Yellow, BlinkKind.None, "任务进行中", "自检：黄灯常亮"),
                new LightState(LightMode.Yellow, BlinkKind.YellowPulse, "等待你确认", "自检：黄灯呼吸"),
                new LightState(LightMode.Red, BlinkKind.None, "需要你处理", "自检：红灯常亮"),
                new LightState(LightMode.Red, BlinkKind.RedFast, "任务失败", "自检：红灯快闪"),
                new LightState(LightMode.Green, BlinkKind.GreenSoft, "任务完成", "自检：绿灯柔闪"),
                new LightState(LightMode.Green, BlinkKind.None, "全部完成", "自检：绿灯常亮"),
            };

            if (!string.IsNullOrEmpty(_fixedState))
            {
                var map = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    { "yellow", 0 }, { "pulse", 1 }, { "red", 2 }, { "redfast", 3 },
                    { "green", 5 }, { "greensoft", 4 }, { "done", 4 },
                };
                int index;
                if (!map.TryGetValue(_fixedState, out index)) index = 0;
                _window.Apply(states[index]);
                return;
            }

            _window.Apply(states[0]);
            _selfTestTimer = new Timer { Interval = 3000 };
            _selfTestTimer.Tick += (s, e) =>
            {
                _selfTestIndex = (_selfTestIndex + 1) % states.Length;
                _window.Apply(states[_selfTestIndex]);
            };
            _selfTestTimer.Start();
        }

        // ------------------------------------------------------------- 菜单动作

        private ContextMenuStrip BuildTrayMenu()
        {
            var menu = new ContextMenuStrip { ShowImageMargin = false };

            var show = new ToolStripMenuItem("显示红绿灯");
            show.Click += (s, e) => ShowWindow();
            menu.Items.Add(show);

            var refresh = new ToolStripMenuItem("立即刷新");
            refresh.Click += (s, e) => PollTick();
            menu.Items.Add(refresh);

            menu.Items.Add(new ToolStripSeparator());

            var quit = new ToolStripMenuItem("退出");
            quit.Click += (s, e) => Quit();
            menu.Items.Add(quit);

            return menu;
        }

        private void ShowWindow()
        {
            if (!_positionReady)
            {
                // 句柄创建后才能拿到真实 DPI，此时再算位置
                _window.MoveTo(InitialLocation());
                _positionReady = true;
            }

            if (Settings.IsHiddenPauseActive() && OpenCodeProcess.IsRunning())
            {
                // 用户主动在托盘里点「显示」：解除本次暂停
                Settings.ClearHidden();
            }

            _forceShowUntil = DateTime.UtcNow.AddSeconds(30);
            _window.Show();
            _window.MoveTo(_window.Location);
            PollTick();
        }

        private void HideTemporarily()
        {
            Settings.MarkHidden();
            _window.Hide();
        }

        private void OpenOpenCode()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            var candidates = new[]
            {
                Path.Combine(local, "Programs", "opencode", "opencode.exe"),
                Path.Combine(local, "Programs", "OpenCode", "OpenCode.exe"),
                Path.Combine(programFiles, "OpenCode", "OpenCode.exe"),
            };

            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;
                try { Process.Start(path); return; }
                catch (Exception) { }
            }

            try
            {
                Process.Start(new ProcessStartInfo("opencode") { UseShellExecute = true });
                return;
            }
            catch (Exception) { }

            try { Process.Start("https://opencode.ai"); }
            catch (Exception) { }
        }

        private void Quit()
        {
            if (_disposed) return;
            _disposed = true;
            try { _pollTimer.Stop(); } catch (Exception) { }
            try { _watchTimer.Stop(); } catch (Exception) { }
            try { _tray.Visible = false; _tray.Dispose(); } catch (Exception) { }
            try { _window.Close(); } catch (Exception) { }
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Quit();
            base.Dispose(disposing);
        }
    }
}
