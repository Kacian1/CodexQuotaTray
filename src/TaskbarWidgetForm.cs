using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Automation;

namespace CodexQuotaTray
{
    internal sealed class TaskbarWidgetForm : Form
    {
        private const int SingleBaseWidth = 97;
        private const int DualBaseWidth = 122;
        private const int BaseHeight = 38;
        private const int TraySafetyReserveLogical = 270; // 100% DPI 下的最小托盘安全区
        private static readonly Color TaskbarBackground = Color.FromArgb(28, 28, 28);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr window, int index, int value);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        private int _fiveHourRemainingPercent;
        private int _fiveHourTimeRemainingPercent = -1;
        private int _weeklyRemainingPercent;
        private DateTimeOffset? _fiveHourResetsAt;
        private bool _hasWeeklyWindow;
        private bool _active;
        private bool _overlaySuppressed;
        private bool _blinkHighlight;
        private float _dpiScale = 1f;
        private AppSettings _settings = new AppSettings();
        private ThemePalette _palette;
        private IntPtr _taskbarHandle;
        private bool _ownerAttachFailed;
        private IntPtr _ownerAttachTaskbarHandle;
        private NativeRect _refreshedTaskButtonBounds;
        private NativeRect _refreshedNotificationBounds;
        private IntPtr _refreshedTaskbarHandle;
        private bool _hasRefreshedTaskButtonBounds;
        private bool _hasRefreshedNotificationBounds;
        private int _dynamicAnchorRefreshRunning;
        private bool _dragTracking;
        private bool _dragging;
        private bool _suppressClick;
        private Point _dragOrigin;
        private Point _dragWindowOrigin;
        private double _dragRatio;
        public event Action ManualPositionChanged;

        public TaskbarWidgetForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            _palette = ThemePalette.FromSettings(_settings);
            ClientSize = new Size(SingleBaseWidth, BaseHeight);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = TaskbarBackground;
            Cursor = Cursors.Hand;
            SetRoundedRegion();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                // WinForms does not reliably put WS_EX_TOPMOST into CreateParams
                // when TopMost is set before the handle exists. Without this bit
                // the widget is shown, but sits behind the taskbar/fullscreen
                // window and looks like it never appeared.
                const int WsExToolWindow = 0x00000080;
                const int WsExNoActivate = 0x08000000;
                const int WsExTopMost = 0x00000008;
                var parameters = base.CreateParams;
                parameters.ExStyle |= WsExToolWindow | WsExNoActivate | WsExTopMost;
                return parameters;
            }
        }

        public void ApplyResult(QuotaReadResult result)
        {
            _active = result != null && result.IsCodexRunning && result.Snapshot != null;
            var primary = result == null || result.Snapshot == null ? null : result.Snapshot.FiveHourWindow;
            var weekly = result == null || result.Snapshot == null ? null : result.Snapshot.DisplayWeeklyWindow;
            _fiveHourRemainingPercent = primary == null ? 0 : primary.RemainingPercent;
            _fiveHourTimeRemainingPercent = primary == null ? -1 : primary.TimeRemainingPercent;
            _weeklyRemainingPercent = weekly == null ? 0 : weekly.RemainingPercent;
            _fiveHourResetsAt = primary == null ? null : primary.ResetsAt;
            _hasWeeklyWindow = weekly != null && weekly != primary;
            ApplyScaledSize();
            Reposition();
            Invalidate();
        }

        public void ApplySettings(AppSettings settings)
        {
            _settings = settings ?? new AppSettings();
            _palette = ThemePalette.FromSettings(_settings);
            BackColor = TaskbarBackground;
            ApplyScaledSize();
            Reposition();
            Invalidate();
        }

        public void SetVisible(bool visible)
        {
            if (visible)
            {
                if (_overlaySuppressed) return;
                EnsureHandle();
                if (!EnsureTaskbarAttachment()) return;
                ShowAndPosition();
            }
            else
            {
                Hide();
            }
        }

        /// <summary>
        /// 鎻愬墠鍒涘缓绐楀彛鍙ユ焺銆傚嵆浣垮惎鍔ㄦ椂姝ｅ浜庡叏灞忋€佹垨浠诲姟鏍忓皻鏈氨缁紝
        /// 涔熶繚璇佹湁涓€涓彲鎭㈠鐨勭獥鍙ｏ紝鑰屼笉鏄瓑鈥滀笅娆℃湁鏈轰細鏄剧ず鈥濇椂鎵嶅垱寤恒€?        /// </summary>
        public void EnsureHandle()
        {
            if (!IsHandleCreated && !IsDisposed)
            {
                var handle = Handle;
                GC.KeepAlive(handle);
            }
        }

        /// <summary>
        /// 鍏ㄥ睆鎶戝埗闂搁棬:缃綅鍚庢偓娴獥淇濇寔闅愯棌,鏈熼棿浠讳綍 Show/Reposition 璺緞閮戒笉浼氶噸鏂版樉绀哄畠
        /// (瀹堟姢瀹氭椂鍣ㄣ€佸悗鍙伴搴﹀埛鏂扮瓑瑙﹀彂鐨?EnsureOnTop/Reposition 鍏ㄩ儴琚嫤鎴?銆?        /// 瑙ｉ櫎鎶戝埗涓嶅湪姝ゅ鎭㈠鏄剧ず,鐢卞畧鎶ゅ畾鏃跺櫒鍚屼竴 tick 鍐呯殑 EnsureOnTop 缁熶竴鎭㈠銆?        /// </summary>
        public void SetOverlaySuppressed(bool suppressed)
        {
            if (_overlaySuppressed == suppressed) return;
            _overlaySuppressed = suppressed;
            if (suppressed)
            {
                CancelDrag();
                try { if (Visible) Hide(); }
                catch { }
            }
        }

        /// <summary>AIHOT 鏂伴€氱煡闂儊楂樹寒(鍙敼搴曡壊,涓嶆敼鏁板瓧鍐呭)銆?/summary>
        public void SetBlinkHighlight(bool highlighted)
        {
            if (_blinkHighlight == highlighted) return;
            _blinkHighlight = highlighted;
            try { Invalidate(); }
            catch { }
        }

        public void EnsureOnTop()
        {
            if (_overlaySuppressed || IsDisposed) return;
            EnsureHandle();
            if (!EnsureTaskbarAttachment()) return;
            ShowAndPosition();
        }

        /// <summary>
        /// 鍏堟妸绐楀彛绉诲埌浠诲姟鏍忎綅缃紝鍐嶆樉绀猴紝鏈€鍚庨噸鏂扮疆椤躲€?        /// 鍏堟樉绀哄啀瀹氫綅浼氱湅鍒颁竴娆?(0,0) 闂烦锛涜€?WinForms 鐨?Show() 浼氭寜
        /// TopMost 灞炴€ч噸鍒锋墿灞曟牱寮忥紝鎵€浠ユ樉绀哄悗瑕佸啀瀹氫綅涓€娆°€?        /// </summary>
        private void ShowAndPosition()
        {
            if (!Visible)
            {
                Reposition(true, false);
                Show();
            }
            Reposition(true, true);
        }

        public void RefreshDynamicAnchor()
        {
            if (_settings.WidgetManualPosition || _dragging || _overlaySuppressed || IsDisposed) return;
            if (Interlocked.Exchange(ref _dynamicAnchorRefreshRunning, 1) != 0) return;
            var thread = new Thread(new ThreadStart(delegate
            {
                try
                {
                    IntPtr taskbar;
                    NativeRect refreshedTaskButtons;
                    NativeRect refreshedNotifications;
                    bool hasTaskButtons, hasNotifications;
                    if (!ReadFreshAnchor(out taskbar, out refreshedTaskButtons, out hasTaskButtons,
                        out refreshedNotifications, out hasNotifications)) return;
                    if (!hasTaskButtons && !hasNotifications) return;
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(new Action(delegate
                    {
                        if (IsDisposed || _settings.WidgetManualPosition || _dragging || _overlaySuppressed) return;
                        _refreshedTaskbarHandle = taskbar;
                        if (hasTaskButtons)
                        {
                            _refreshedTaskButtonBounds = refreshedTaskButtons;
                            _hasRefreshedTaskButtonBounds = true;
                        }
                        if (hasNotifications)
                        {
                            _refreshedNotificationBounds = refreshedNotifications;
                            _hasRefreshedNotificationBounds = true;
                        }
                        Reposition(true);
                    }));
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                catch (IOException) { }
                finally { Interlocked.Exchange(ref _dynamicAnchorRefreshRunning, 0); }
            }));
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        // A fresh UIA client process avoids stale provider geometry retained by the
        // long-lived tray process. This helper uses the same EXE, with bounded lifetime.
        public static void WriteAnchorProbeResult()
        {
            var taskbar = FindWindow("Shell_TrayWnd", null);
            NativeRect bounds;
            if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out bounds)) return;
            var horizontal = bounds.Right - bounds.Left >= bounds.Bottom - bounds.Top;
            NativeRect buttons, notifications;
            var hasButtons = TryGetTaskButtonBounds(taskbar, bounds, horizontal, out buttons);
            var hasNotifications = TryGetVisibleNotificationBounds(taskbar, bounds, horizontal, out notifications);
            Console.WriteLine(string.Join("|", new[] { "CQT_ANCHOR_V1", taskbar.ToInt64().ToString(CultureInfo.InvariantCulture),
                hasButtons ? "1" : "0", buttons.Left.ToString(CultureInfo.InvariantCulture), buttons.Top.ToString(CultureInfo.InvariantCulture),
                buttons.Right.ToString(CultureInfo.InvariantCulture), buttons.Bottom.ToString(CultureInfo.InvariantCulture),
                hasNotifications ? "1" : "0", notifications.Left.ToString(CultureInfo.InvariantCulture), notifications.Top.ToString(CultureInfo.InvariantCulture),
                notifications.Right.ToString(CultureInfo.InvariantCulture), notifications.Bottom.ToString(CultureInfo.InvariantCulture) }));
        }

        private static bool ReadFreshAnchor(out IntPtr taskbar, out NativeRect buttons, out bool hasButtons,
            out NativeRect notifications, out bool hasNotifications)
        {
            taskbar = IntPtr.Zero;
            buttons = notifications = new NativeRect();
            hasButtons = hasNotifications = false;
            using (var probe = new Process())
            {
                probe.StartInfo = new ProcessStartInfo(Application.ExecutablePath, "--anchor-probe")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
                };
                probe.Start();
                if (!probe.WaitForExit(2500))
                {
                    try { probe.Kill(); probe.WaitForExit(500); } catch { }
                    return false;
                }
                if (probe.ExitCode != 0) return false;
                var fields = probe.StandardOutput.ReadToEnd().Trim().Split('|');
                if (fields.Length != 12 || fields[0] != "CQT_ANCHOR_V1") return false;
                long handle;
                if (!long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out handle)) return false;
                var values = new int[10];
                for (var i = 0; i < values.Length; i++)
                    if (!int.TryParse(fields[i + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i])) return false;
                taskbar = new IntPtr(handle);
                if (taskbar != FindWindow("Shell_TrayWnd", null)) return false;
                hasButtons = values[0] == 1;
                buttons = new NativeRect { Left = values[1], Top = values[2], Right = values[3], Bottom = values[4] };
                hasNotifications = values[5] == 1;
                notifications = new NativeRect { Left = values[6], Top = values[7], Right = values[8], Bottom = values[9] };
                return true;
            }
        }

        public Rectangle GetAnchorBounds()
        {
            NativeRect bounds;
            if (IsHandleCreated && GetWindowRect(Handle, out bounds))
            {
                return Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            }

            return new Rectangle(Left, Top, Width, Height);
        }

        public void Reposition(bool forceZOrder = false, bool show = false)
        {
            if (_overlaySuppressed || _dragging) return;
            var taskbar = FindWindow("Shell_TrayWnd", null);
            NativeRect taskbarBounds;
            if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out taskbarBounds))
            {
                return;
            }

            if (!EnsureTaskbarAttachment()) return;

            var taskbarWidth = taskbarBounds.Right - taskbarBounds.Left;
            var taskbarHeight = taskbarBounds.Bottom - taskbarBounds.Top;
            var gap = Math.Max(8, (int)Math.Round(8 * _dpiScale));
            // 270 logical px is the floor. The real TrayNotifyWnd width wins
            // when it is larger, so adding/removing tray icons moves the widget
            // left/right automatically instead of relying on a fixed offset.
            var trayReserve = (int)Math.Round(TraySafetyReserveLogical * _dpiScale);
            NativeRect trayBounds;
            var hasTray = TryGetTrayBounds(taskbar, out trayBounds) && IntersectsTaskbar(trayBounds, taskbarBounds);
            if (!hasTray && _hasRefreshedNotificationBounds && _refreshedTaskbarHandle == taskbar &&
                IntersectsTaskbar(_refreshedNotificationBounds, taskbarBounds))
            {
                trayBounds = _refreshedNotificationBounds;
                hasTray = true;
            }
            var trayLeftEdge = hasTray ? Math.Min(trayBounds.Left, taskbarBounds.Right - trayReserve) : 0;
            var trayTopEdge = hasTray ? Math.Min(trayBounds.Top, taskbarBounds.Bottom - trayReserve) : 0;
            NativeRect taskButtonBounds;
            var hasTaskButtons = _hasRefreshedTaskButtonBounds && _refreshedTaskbarHandle == taskbar &&
                IntersectsTaskbar(_refreshedTaskButtonBounds, taskbarBounds);
            taskButtonBounds = hasTaskButtons ? _refreshedTaskButtonBounds : new NativeRect();
            // UIA reads are asynchronous. Never block the UI/fullscreen timer here.

            int x;
            int y;
            if (taskbarWidth >= taskbarHeight)
            {
                // Auto placement always sits immediately left of the whole tray,
                // not left of the first visible icon. Task buttons are only a
                // fallback when the tray window is unavailable.
                x = hasTray
                    ? trayLeftEdge - Width - gap
                    : (hasTaskButtons
                        ? CalculateLeadingPlacement(taskbarBounds.Left, taskbarBounds.Right, Width, taskButtonBounds.Left, gap)
                        : taskbarBounds.Right - Width - Math.Max(gap, trayReserve));
                y = taskbarBounds.Top + Math.Max(0, (taskbarHeight - Height) / 2) + Math.Max(1, (int)Math.Round(_dpiScale));
                y = Math.Min(y, taskbarBounds.Bottom - Height - 1);
                if (_settings.WidgetManualPosition)
                {
                    x = WidgetPlacement.Coordinate(taskbarBounds.Left, taskbarBounds.Right, Width, _settings.WidgetPositionRatio);
                    if (hasTray)
                        x = WidgetPlacement.ClampBeforeTray(x, taskbarBounds.Left, trayLeftEdge, Width, gap);
                }
                x = Math.Max(taskbarBounds.Left + 4, Math.Min(x, taskbarBounds.Right - Width - 4));
            }
            else
            {
                x = taskbarBounds.Left + Math.Max(0, (taskbarWidth - Width) / 2);
                y = hasTray
                    ? trayTopEdge - Height - gap
                    : (hasTaskButtons
                        ? CalculateLeadingPlacement(taskbarBounds.Top, taskbarBounds.Bottom, Height, taskButtonBounds.Top, gap)
                        : taskbarBounds.Bottom - Height - Math.Max(gap, trayReserve));
                if (_settings.WidgetManualPosition)
                {
                    y = WidgetPlacement.Coordinate(taskbarBounds.Top, taskbarBounds.Bottom, Height, _settings.WidgetPositionRatio);
                    if (hasTray)
                        y = WidgetPlacement.ClampBeforeTray(y, taskbarBounds.Top, trayTopEdge, Height, gap);
                }
                y = Math.Max(taskbarBounds.Top + 4, Math.Min(y, taskbarBounds.Bottom - Height - 4));
            }

            NativeRect currentBounds;
            const int extendedStyle = -20;
            const int topMostStyle = 0x00000008;
            var correctlyPlaced = GetWindowRect(Handle, out currentBounds) &&
                currentBounds.Left == x && currentBounds.Top == y &&
                currentBounds.Right - currentBounds.Left == Width &&
                currentBounds.Bottom - currentBounds.Top == Height;
            var alreadyTopMost = (GetWindowLong(Handle, extendedStyle) & topMostStyle) != 0;
            if (!show && correctlyPlaced && alreadyTopMost && !forceZOrder) return;

            const uint noActivate = 0x0010;
            const uint showWindow = 0x0040;
            var topMost = new IntPtr(-1);
            var flags = noActivate;
            if (show) flags |= showWindow;
            SetWindowPos(
                Handle,
                topMost,
                x,
                y,
                Width,
                Height,
                flags);
        }

        internal static int CalculateLeadingPlacement(int taskbarStart, int taskbarEnd, int widgetExtent, int leadingEdge, int gap)
        {
            return Math.Max(taskbarStart + 4, Math.Min(leadingEdge - widgetExtent - gap, taskbarEnd - widgetExtent - 4));
        }

        private static bool TryGetTaskButtonBounds(
            IntPtr taskbar,
            NativeRect taskbarBounds,
            bool horizontal,
            out NativeRect result)
        {
            result = new NativeRect();
            try
            {
                var root = FindTaskbarAutomationRoot(taskbar);
                if (root == null) return false;

                var buttons = root.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                var found = false;
                var leadingEdge = int.MaxValue;
                for (var index = 0; index < buttons.Count; index++)
                {
                    var current = buttons[index].Current;
                    if (current.IsOffscreen || !IsTaskButton(current.AutomationId)) continue;
                    var bounds = current.BoundingRectangle;
                    if (bounds.IsEmpty || bounds.Width < 4 || bounds.Height < 4 || bounds.Width > 240 || bounds.Height > 240) continue;
                    var candidate = ToNativeRect(bounds);
                    if (!IntersectsTaskbar(candidate, taskbarBounds)) continue;

                    var edge = horizontal ? candidate.Left : candidate.Top;
                    if (edge >= leadingEdge) continue;
                    leadingEdge = edge;
                    result = candidate;
                    found = true;
                }
                return found;
            }
            catch (ElementNotAvailableException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (COMException) { return false; }
        }

        private static bool TryGetVisibleNotificationBounds(
            IntPtr taskbar,
            NativeRect taskbarBounds,
            bool horizontal,
            out NativeRect result)
        {
            result = new NativeRect();
            try
            {
                var root = FindTaskbarAutomationRoot(taskbar);
                if (root == null) return false;

                var buttons = root.FindAll(
                    TreeScope.Descendants,
                    new OrCondition(
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayIcon"),
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "NotifyItemIcon")));
                var found = false;
                var leadingEdge = int.MaxValue;
                for (var index = 0; index < buttons.Count; index++)
                {
                    var current = buttons[index].Current;
                    if (current.IsOffscreen) continue;
                    var bounds = current.BoundingRectangle;
                    if (bounds.IsEmpty || bounds.Width < 4 || bounds.Height < 4 || bounds.Width > 200 || bounds.Height > 200) continue;
                    var candidate = ToNativeRect(bounds);
                    if (!IntersectsTaskbar(candidate, taskbarBounds)) continue;

                    var edge = horizontal ? candidate.Left : candidate.Top;
                    if (edge >= leadingEdge) continue;
                    leadingEdge = edge;
                    result = candidate;
                    found = true;
                }
                return found;
            }
            catch (ElementNotAvailableException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (COMException) { return false; }
        }

        private static AutomationElement FindTaskbarAutomationRoot(IntPtr taskbar)
        {
            var root = AutomationElement.RootElement.FindFirst(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
            return root ?? AutomationElement.FromHandle(taskbar);
        }

        private static bool IsTaskButton(string automationId)
        {
            return string.Equals(automationId, "StartButton", StringComparison.Ordinal) ||
                string.Equals(automationId, "SearchButton", StringComparison.Ordinal) ||
                string.Equals(automationId, "TaskViewButton", StringComparison.Ordinal) ||
                string.Equals(automationId, "WidgetsButton", StringComparison.Ordinal) ||
                (!string.IsNullOrEmpty(automationId) && automationId.StartsWith("Appid:", StringComparison.Ordinal));
        }

        private static NativeRect ToNativeRect(System.Windows.Rect bounds)
        {
            return new NativeRect
            {
                Left = (int)Math.Round(bounds.Left),
                Top = (int)Math.Round(bounds.Top),
                Right = (int)Math.Round(bounds.Right),
                Bottom = (int)Math.Round(bounds.Bottom)
            };
        }

        private static bool IntersectsTaskbar(NativeRect candidate, NativeRect taskbar)
        {
            return candidate.Right > taskbar.Left && candidate.Left < taskbar.Right &&
                candidate.Bottom > taskbar.Top && candidate.Top < taskbar.Bottom;
        }

        private static bool TryGetTrayBounds(IntPtr taskbar, out NativeRect bounds)
        {
            bounds = new NativeRect();
            var tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            return tray != IntPtr.Zero && GetWindowRect(tray, out bounds);
        }

        private bool EnsureTaskbarAttachment()
        {
            const int ownerIndex = -8;
            const uint ownerWindow = 4;

            var taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) return false;

            var handle = Handle;
            _taskbarHandle = taskbar;

            // Setting GWLP_HWNDPARENT to a window in another process is not
            // allowed on current Windows: it fails with ERROR_INVALID_PARAMETER
            // and leaves the WinForms parking window as owner. Treat the owner
            // as an optional z-order optimization only. Visibility and
            // positioning must never depend on it, otherwise the widget stays
            // invisible at (0,0) forever.
            if ((!_ownerAttachFailed || _ownerAttachTaskbarHandle != taskbar) &&
                GetWindow(handle, ownerWindow) != taskbar)
            {
                _ownerAttachTaskbarHandle = taskbar;
                try { SetWindowLongPtr(handle, ownerIndex, taskbar); }
                catch { }
                _ownerAttachFailed = GetWindow(handle, ownerWindow) != taskbar;
            }

            var scale = GraphicsExtensions.GetDpiScale(handle);
            if (Math.Abs(scale - _dpiScale) > 0.001f)
            {
                _dpiScale = scale;
                ApplyScaledSize();
            }

            // A missing taskbar is the only condition that should block
            // placement. The owner-attachment result is intentionally ignored.
            return true;
        }

        private static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value)
        {
            if (IntPtr.Size == 8) return SetWindowLongPtr64(window, index, value);
            return new IntPtr(SetWindowLong(window, index, value.ToInt32()));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            SetRoundedRegion();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _suppressClick = false;
            if (e.Button == MouseButtons.Left && !_overlaySuppressed)
            {
                _dragOrigin = Cursor.Position;
                _dragWindowOrigin = Location;
                _dragTracking = true;
                Capture = true;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragTracking && Capture && (e.Button & MouseButtons.Left) != 0 && !_overlaySuppressed)
            {
                var cursor = Cursor.Position;
                if (!_dragging && WidgetPlacement.StartsDrag(_dragOrigin, cursor, SystemInformation.DragSize))
                {
                    _dragging = true;
                    _suppressClick = true;
                    Cursor = Cursors.SizeAll;
                }
                if (_dragging)
                {
                    NativeRect taskbar;
                    var taskbarHandle = FindWindow("Shell_TrayWnd", null);
                    if (taskbarHandle != IntPtr.Zero && GetWindowRect(taskbarHandle, out taskbar))
                    {
                        var horizontal = taskbar.Right - taskbar.Left >= taskbar.Bottom - taskbar.Top;
                        var gap = Math.Max(8, (int)Math.Round(8 * _dpiScale));
                        var trayReserve = (int)Math.Round(TraySafetyReserveLogical * _dpiScale);
                        NativeRect trayBounds;
                        var hasTray = TryGetTrayBounds(taskbarHandle, out trayBounds) && IntersectsTaskbar(trayBounds, taskbar);
                        var coordinate = horizontal ? _dragWindowOrigin.X + cursor.X - _dragOrigin.X : _dragWindowOrigin.Y + cursor.Y - _dragOrigin.Y;
                        _dragRatio = horizontal
                            ? WidgetPlacement.Ratio(coordinate, taskbar.Left, taskbar.Right, Width)
                            : WidgetPlacement.Ratio(coordinate, taskbar.Top, taskbar.Bottom, Height);
                        var x = horizontal ? WidgetPlacement.Coordinate(taskbar.Left, taskbar.Right, Width, _dragRatio) : Left;
                        var y = horizontal ? Top : WidgetPlacement.Coordinate(taskbar.Top, taskbar.Bottom, Height, _dragRatio);
                        if (horizontal && hasTray)
                        {
                            var trayEdge = Math.Min(trayBounds.Left, taskbar.Right - trayReserve);
                            var maximum = Math.Max(taskbar.Left + 4, trayEdge - Width - gap);
                            if (x > maximum)
                            {
                                x = maximum;
                                _dragRatio = WidgetPlacement.Ratio(x, taskbar.Left, taskbar.Right, Width);
                            }
                        }
                        else if (!horizontal && hasTray)
                        {
                            var trayEdge = Math.Min(trayBounds.Top, taskbar.Bottom - trayReserve);
                            var maximum = Math.Max(taskbar.Top + 4, trayEdge - Height - gap);
                            if (y > maximum)
                            {
                                y = maximum;
                                _dragRatio = WidgetPlacement.Ratio(y, taskbar.Top, taskbar.Bottom, Height);
                            }
                        }
                        SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0, 0x0015); // no size/z-order/activation
                    }
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _dragTracking)
            {
                var moved = _dragging;
                _dragTracking = false;
                _dragging = false;
                Capture = false;
                Cursor = Cursors.Hand;
                if (moved && !_overlaySuppressed)
                {
                    _settings.WidgetManualPosition = true;
                    _settings.WidgetPositionRatio = _dragRatio;
                    var handler = ManualPositionChanged;
                    if (handler != null) handler();
                    Reposition();
                }
            }
            base.OnMouseUp(e);
        }

        protected override void OnClick(EventArgs e)
        {
            if (!_suppressClick && !_dragging) base.OnClick(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (!Capture && _dragTracking) CancelDrag();
            base.OnMouseCaptureChanged(e);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            if (!Visible) CancelDrag();
            base.OnVisibleChanged(e);
        }

        private void CancelDrag()
        {
            if (!_dragTracking) return;
            _dragTracking = false;
            _dragging = false;
            Capture = false;
            Cursor = Cursors.Hand;
            if (IsHandleCreated && !IsDisposed) Reposition();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _dpiScale = GraphicsExtensions.GetDpiScale(Handle);
            ApplyScaledSize();
            if (_settings.WidgetVisible)
                BeginInvoke(new Action(delegate { EnsureOnTop(); }));
        }

        protected override void WndProc(ref Message message)
        {
            const int DpiChanged = 0x02E0;
            if (message.Msg == DpiChanged)
            {
                var newDpi = message.WParam.ToInt64() & 0xFFFF;
                base.WndProc(ref message);
                _dpiScale = Math.Max(1f, newDpi / 96f);
                ApplyScaledSize();
                BeginInvoke(new Action(delegate { Reposition(); }));
                Invalidate();
                return;
            }
            base.WndProc(ref message);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var scale = GetDrawingScale();
            graphics.ScaleTransform(scale, scale);

            var accent = _active ? _palette.QuotaColor(_fiveHourRemainingPercent) : _palette.Faint;
            var weeklyAccent = _active ? _palette.QuotaColor(_weeklyRemainingPercent) : _palette.Faint;
            var baseWidth = GetBaseWidth();
            var backgroundColor = TaskbarBackground;
            if (_blinkHighlight)
                backgroundColor = Blend(backgroundColor, _palette.Primary, _palette.IsLight ? 0.14f : 0.20f);
            using (var background = new SolidBrush(backgroundColor))
            {
                graphics.FillRectangle(background, 0, 0, baseWidth, BaseHeight);
            }

            using (var trackPen = new Pen(_palette.Track, 3f))
            using (var fillPen = new Pen(accent, 3f))
            {
                trackPen.StartCap = trackPen.EndCap = LineCap.Round;
                fillPen.StartCap = fillPen.EndCap = LineCap.Round;
                var ring = new RectangleF(9, 8, 25, 25);
                graphics.DrawArc(trackPen, ring, -90, 359.9f);
                if (_active && _fiveHourRemainingPercent > 0)
                {
                    graphics.DrawArc(fillPen, ring, -90, 360f * _fiveHourRemainingPercent / 100f);
                }
            }

            if (_active && _fiveHourTimeRemainingPercent >= 0)
            {
                using (var trackPen = new Pen(_palette.Faint, 1.6f))
                using (var fillPen = new Pen(_palette.Secondary, 1.6f))
                {
                    trackPen.StartCap = trackPen.EndCap = LineCap.Round;
                    fillPen.StartCap = fillPen.EndCap = LineCap.Round;
                    var ring = new RectangleF(13, 12, 17, 17);
                    graphics.DrawArc(trackPen, ring, -90, 359.9f);
                    if (_fiveHourTimeRemainingPercent > 0)
                        graphics.DrawArc(fillPen, ring, -90, 360f * _fiveHourTimeRemainingPercent / 100f);
                }
            }

            if (_hasWeeklyWindow)
            {
                using (var trackPen = new Pen(_palette.Track, 2.2f))
                using (var fillPen = new Pen(weeklyAccent, 2.2f))
                {
                    trackPen.StartCap = trackPen.EndCap = LineCap.Round;
                    fillPen.StartCap = fillPen.EndCap = LineCap.Round;
                    var ring = new RectangleF(96, 10, 17, 17);
                    graphics.DrawArc(trackPen, ring, -90, 359.9f);
                    if (_active && _weeklyRemainingPercent > 0)
                        graphics.DrawArc(fillPen, ring, -90, 360f * _weeklyRemainingPercent / 100f);
                }
            }

            var value = _active ? _fiveHourRemainingPercent + "%" : "--%";
            var resetHours = _active ? FormatResetHours(_fiveHourResetsAt, DateTimeOffset.Now) : "--H";
            var fontScale = _settings.FontScalePercent / 100f;
            using (var valueFont = new Font("Segoe UI", 12.5f * 96f / 72f * fontScale, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var labelFont = new Font("Segoe UI", 6.8f * 96f / 72f * fontScale, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var weeklyFont = new Font("Segoe UI", 5.8f * 96f / 72f * fontScale, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var valueBrush = new SolidBrush(_palette.Text))
            using (var labelBrush = new SolidBrush(_active ? accent : _palette.Muted))
            using (var weeklyBrush = new SolidBrush(_active ? weeklyAccent : _palette.Muted))
            {
                graphics.DrawString(value, valueFont, valueBrush, new RectangleF(42, 2, 52, 25));
                graphics.DrawString(_active ? resetHours : "OFFLINE", labelFont, labelBrush, new RectangleF(43, 23, 48, 14));
                if (_hasWeeklyWindow)
                {
                    using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        graphics.DrawString(_weeklyRemainingPercent.ToString(CultureInfo.InvariantCulture), weeklyFont, weeklyBrush, new RectangleF(95, 9, 20, 20), format);
                }
            }
        }

        internal static string FormatResetHours(DateTimeOffset? resetsAt, DateTimeOffset now)
        {
            if (!resetsAt.HasValue) return "--H";
            var hours = Math.Max(0d, (resetsAt.Value - now).TotalHours);
            return hours.ToString("0.#", CultureInfo.InvariantCulture) + "H";
        }

        private void SetRoundedRegion()
        {
            using (var path = GraphicsExtensions.CreateRoundedPath(
                new RectangleF(0, 0, Width, Height),
                Math.Max(8f, 8f * GetDrawingScale())))
            {
                Region = new Region(path);
            }
        }

        private static Color Blend(Color from, Color to, float amount)
        {
            amount = Math.Max(0f, Math.Min(1f, amount));
            return Color.FromArgb(
                (int)Math.Round(from.A + (to.A - from.A) * amount),
                (int)Math.Round(from.R + (to.R - from.R) * amount),
                (int)Math.Round(from.G + (to.G - from.G) * amount),
                (int)Math.Round(from.B + (to.B - from.B) * amount));
        }

        private float GetDrawingScale()
        {
            return _dpiScale * _settings.WidgetScalePercent / 100f;
        }

        private Size GetScaledSize()
        {
            return GraphicsExtensions.ScaleSize(new Size(GetBaseWidth(), BaseHeight), GetDrawingScale());
        }

        private int GetBaseWidth()
        {
            return _hasWeeklyWindow ? DualBaseWidth : SingleBaseWidth;
        }

        private void ApplyScaledSize()
        {
            if (!IsHandleCreated) return;
            Size = GetScaledSize();
            SetRoundedRegion();
        }
    }
}

