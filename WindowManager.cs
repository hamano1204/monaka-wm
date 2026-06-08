using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using monaka_wm.Services;
using Application = System.Windows.Application;

namespace monaka_wm
{
    public enum SplitDirection
    {
        Horizontal,
        Vertical
    }

    public class WindowManager : DependencyObject
    {
        private static WindowManager? _instance;
        public static WindowManager Instance => _instance ??= new WindowManager();

        private readonly Dictionary<string, SplitDirection> _currentMonitorSplitDirections = new();

        public event Action<string, SplitDirection>? SplitDirectionChanged;

        public SplitDirection GetSplitDirection(string monitorName)
        {
            if (string.IsNullOrEmpty(monitorName)) return SplitDirection.Horizontal;
            if (_currentMonitorSplitDirections.TryGetValue(monitorName, out var dir))
            {
                return dir;
            }
            return SplitDirection.Horizontal;
        }

        public void SetSplitDirection(string monitorName, SplitDirection dir)
        {
            if (string.IsNullOrEmpty(monitorName)) return;
            _currentMonitorSplitDirections[monitorName] = dir;
            SplitDirectionChanged?.Invoke(monitorName, dir);
            DeferApplyLayout();
        }

        private readonly WindowHookService _hookService;
        private readonly VirtualDesktopService _desktopService;
        private readonly LayoutEngine _layoutEngine;

        private readonly HashSet<IntPtr> _taskbarHwnds = new();
        public const int TASKBAR_HEIGHT = 45; // Taskbar height in WPF units

        public ObservableCollection<WindowItem> Windows { get; } = new();

        private IntPtr _lastProcessedForegroundHwnd = IntPtr.Zero;
        private bool _isLayoutPending = false;

        private readonly Dictionary<string, WindowItem?> _activeWindowsMap = new();

        public bool IsInitialized { get; private set; }

        public event EventHandler? DesktopChanged
        {
            add => _desktopService.DesktopChanged += value;
            remove => _desktopService.DesktopChanged -= value;
        }

        public bool IsWindowOnCurrentDesktop(IntPtr hWnd)
        {
            return _desktopService.IsWindowOnCurrentDesktop(hWnd);
        }

        private void SwitchToDesktop(Guid newDesktopId)
        {
            try
            {
                if (_desktopService.CurrentDesktopId == newDesktopId) return;

                // 1. Save current desktop state
                if (_desktopService.CurrentDesktopId != Guid.Empty)
                {
                    _desktopService.SaveState(_desktopService.CurrentDesktopId, _activeWindowsMap, Windows);
                    var oldState = _desktopService.GetOrCreateState(_desktopService.CurrentDesktopId);
                    oldState.MonitorSplitDirections.Clear();
                    foreach (var kvp in _currentMonitorSplitDirections)
                    {
                        oldState.MonitorSplitDirections[kvp.Key] = kvp.Value;
                    }
                }

                // 2. Update current desktop ID
                _desktopService.CurrentDesktopId = newDesktopId;

                // Move our WPF bar windows to the new virtual desktop so they remain visible
                foreach (var hwnd in _taskbarHwnds)
                {
                    _desktopService.MoveWindowToDesktop(hwnd, ref newDesktopId);
                }

                // 3. Load or create new desktop state
                var state = _desktopService.GetOrCreateState(newDesktopId);

                // Restore split directions
                _currentMonitorSplitDirections.Clear();
                foreach (var kvp in state.MonitorSplitDirections)
                {
                    _currentMonitorSplitDirections[kvp.Key] = kvp.Value;
                }

                // Raise split direction changed for all screens to refresh UI
                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                {
                    var dir = GetSplitDirection(screen.DeviceName);
                    SplitDirectionChanged?.Invoke(screen.DeviceName, dir);
                }

                // 4. Restore ColumnIndex for managed windows on the new desktop
                foreach (var w in Windows)
                {
                    w.IsOnCurrentDesktop = IsWindowOnCurrentDesktop(w.Handle);
                    if (w.IsOnCurrentDesktop)
                    {
                        if (state.WindowColumns.TryGetValue(w.Handle, out int col))
                        {
                            w.ColumnIndex = col;
                        }
                        else
                        {
                            w.ColumnIndex = 0;
                        }
                    }
                }

                // 5. Restore active windows
                _activeWindowsMap.Clear();
                foreach (var kvp in state.ActiveWindowHandles)
                {
                    var window = Windows.FirstOrDefault(w => w.Handle == kvp.Value);
                    if (window != null && Windows.Contains(window) && _desktopService.IsWindowOnCurrentDesktop(window.Handle))
                    {
                        _activeWindowsMap[kvp.Key] = window;
                    }
                }

                // 6. Notify UI and apply layout
                ScanExistingWindows();
                _desktopService.RaiseDesktopChanged();
                DeferApplyLayout();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error during SwitchToDesktop: {ex.Message}");
            }
        }

        // Dependency Properties for UI Binding
        public static readonly DependencyProperty IsTileModeProperty =
            DependencyProperty.Register(nameof(IsTileMode), typeof(bool), typeof(WindowManager), new PropertyMetadata(true, OnModeChanged));

        public bool IsTileMode
        {
            get => (bool)GetValue(IsTileModeProperty);
            set => SetValue(IsTileModeProperty, value);
        }

        public static readonly DependencyProperty ColumnsCountProperty =
            DependencyProperty.Register(nameof(ColumnsCount), typeof(int), typeof(WindowManager), new PropertyMetadata(1));

        public int ColumnsCount
        {
            get => (int)GetValue(ColumnsCountProperty);
            set => SetValue(ColumnsCountProperty, value);
        }

        private WindowManager()
        {
            _hookService = new WindowHookService(WinEventCallback);
            _desktopService = new VirtualDesktopService();
            _layoutEngine = new LayoutEngine();
        }

        public void Initialize()
        {
            if (IsInitialized) return;
            IsInitialized = true;

            // Initialize current desktop Guid
            var activeHwnd = NativeMethods.GetForegroundWindow();
            Guid desktopId = Guid.Empty;
            if (activeHwnd != IntPtr.Zero)
            {
                desktopId = _desktopService.GetWindowDesktopId(activeHwnd);
            }
            _desktopService.CurrentDesktopId = desktopId;

            ScanExistingWindows();
            DeferApplyLayout();
            _hookService.Start();

            RegisterGlobalHotkeys();
        }

        public void RegisterTaskbarHwnd(IntPtr hWnd)
        {
            _taskbarHwnds.Add(hWnd);
        }

        public void Shutdown()
        {
            _hookService.Stop();
            UnregisterGlobalHotkeys();

            // In Tile mode, restore all windows to normal before exiting
            _layoutEngine.RestoreAllWindows(Windows);
        }

        private void ScanExistingWindows()
        {
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (ShouldManageWindow(hWnd))
                {
                    var existing = Windows.FirstOrDefault(w => w.Handle == hWnd);
                    if (existing == null)
                    {
                        AddWindow(hWnd);
                    }
                    else
                    {
                        existing.IsOnCurrentDesktop = IsWindowOnCurrentDesktop(hWnd);
                        var screen = System.Windows.Forms.Screen.FromHandle(hWnd);
                        existing.MonitorName = screen.DeviceName;
                    }
                }
                return true;
            }, IntPtr.Zero);

            // Remove windows that no longer exist in the OS (closed)
            var toRemove = Windows.Where(w => !NativeMethods.IsWindow(w.Handle)).ToList();
            foreach (var w in toRemove)
            {
                RemoveWindow(w.Handle);
            }
        }

        private bool ShouldManageWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || _taskbarHwnds.Contains(hWnd)) return false;
            if (!NativeMethods.IsWindow(hWnd)) return false;

            StringBuilder title = new StringBuilder(256);
            NativeMethods.GetWindowText(hWnd, title, title.Capacity);
            string titleStr = title.ToString();

            StringBuilder className = new StringBuilder(256);
            NativeMethods.GetClassName(hWnd, className, className.Capacity);
            string cls = className.ToString();

            if (!NativeMethods.IsWindowVisible(hWnd)) return false;

            int style = (int)NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_STYLE);
            int exStyle = (int)NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE);

            if (HasInvalidWindowStyles(style, exStyle)) return false;
            if (IsShellOrSystemWindow(cls)) return false;
            if (IsTooltipOrBalloonNotification(cls)) return false;
            if (IsContextMenuOrJumpList(cls, titleStr)) return false;
            if (string.IsNullOrWhiteSpace(titleStr)) return false;
            if (IsBackgroundExperienceWindow(titleStr)) return false;
            if (IsWindowCloaked(hWnd)) return false;
            if (HasOwnerWithoutAppWindow(hWnd, exStyle)) return false;

            return true;
        }

        private bool HasInvalidWindowStyles(int style, int exStyle)
        {
            if ((style & (int)NativeMethods.WS_CHILD) != 0) return true;
            if ((exStyle & (int)NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;
            return false;
        }

        private bool IsShellOrSystemWindow(string className)
        {
            return className == "Progman" || 
                   className == "WorkerW" || 
                   className == "Shell_TrayWnd" || 
                   className == "Shell_SecondaryTrayWnd";
        }

        private bool IsTooltipOrBalloonNotification(string className)
        {
            return className.Equals("tooltips_class32", StringComparison.OrdinalIgnoreCase) ||
                   className.Contains("tooltip", StringComparison.OrdinalIgnoreCase) ||
                   className.Equals("XamlBalloon", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsContextMenuOrJumpList(string className, string title)
        {
            return className == "#32768" || 
                   className.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase) ||
                   title.Contains("ジャンプ リスト", StringComparison.OrdinalIgnoreCase) ||
                   title.Contains("Jump List", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsBackgroundExperienceWindow(string title)
        {
            return title == "Windows 入力エクスペリエンス" || 
                   title == "Windows Input Experience";
        }

        private bool IsWindowCloaked(IntPtr hWnd)
        {
            if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED, out uint cloaked, sizeof(uint)) == 0)
            {
                return cloaked != 0;
            }
            return false;
        }

        private bool HasOwnerWithoutAppWindow(IntPtr hWnd, int exStyle)
        {
            IntPtr owner = NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER);
            return owner != IntPtr.Zero && (exStyle & (int)NativeMethods.WS_EX_APPWINDOW) == 0;
        }

        private void AddWindow(IntPtr hWnd)
        {
            if (Windows.Any(w => w.Handle == hWnd)) return;

            StringBuilder title = new StringBuilder(256);
            NativeMethods.GetWindowText(hWnd, title, title.Capacity);

            NativeMethods.GetWindowThreadProcessId(hWnd, out uint processId);
            string processName = "Unknown";
            try
            {
                using var proc = Process.GetProcessById((int)processId);
                processName = proc.ProcessName;
            }
            catch { }

            var screen = System.Windows.Forms.Screen.FromHandle(hWnd);
            var item = new WindowItem(hWnd, title.ToString(), processName)
            {
                ColumnIndex = 0,
                IsOnCurrentDesktop = IsWindowOnCurrentDesktop(hWnd),
                MonitorName = screen.DeviceName
            };

            // Capture original position IMMEDIATELY, before ApplyLayout moves it off-screen.
            // At this point the window is still at its real position.
            _layoutEngine.CaptureWindowPlacement(item);

            Windows.Add(item);
        }

        private void RemoveWindow(IntPtr hWnd)
        {
            var item = Windows.FirstOrDefault(w => w.Handle == hWnd);
            if (item != null)
            {
                Windows.Remove(item);

                if (hWnd == _lastProcessedForegroundHwnd)
                {
                    _lastProcessedForegroundHwnd = IntPtr.Zero;
                }
                
                // If it was the active window, update active in dictionary
                string key = $"{item.MonitorName}_{item.ColumnIndex}";
                if (_activeWindowsMap.TryGetValue(key, out var active) && active == item)
                {
                    _activeWindowsMap[key] = null;
                }

                // Clean up original placement cache if the window itself is no longer valid (destroyed/closed)
                if (!NativeMethods.IsWindow(hWnd))
                {
                    _layoutEngine.ClearCachedPlacement(hWnd);
                }

                // Check if the highest column became empty and we should reduce ColumnsCount
                int maxUsedColumn = 0;
                if (Windows.Count > 0)
                {
                    maxUsedColumn = Windows.Max(w => w.ColumnIndex);
                }
                ColumnsCount = Math.Max(1, maxUsedColumn + 1);

                DeferApplyLayout();
            }
        }

        private void UpdateWindowTitle(IntPtr hWnd)
        {
            var item = Windows.FirstOrDefault(w => w.Handle == hWnd);
            if (item != null)
            {
                StringBuilder title = new StringBuilder(256);
                NativeMethods.GetWindowText(hWnd, title, title.Capacity);
                item.Title = title.ToString();
            }
        }

        private bool _isProcessingWinEvent = false;
        // Coalescing counter for foreground events: only process the last event in a burst
        private int _pendingForegroundEvents = 0;

        private void WinEventCallback(uint eventType, IntPtr hWnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (idObject != 0) return;

            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND)
                    {
                        HandleForegroundEvent(hWnd);
                        return;
                    }

                    if (_isProcessingWinEvent) return;
                    _isProcessingWinEvent = true;

                    try
                    {
                        HandleWindowEvent(eventType, hWnd);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error handling window event {eventType}: {ex.Message}");
                    }
                    finally
                    {
                        _isProcessingWinEvent = false;
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in WinEventCallback: {ex.Message}");
            }
        }

        private void HandleForegroundEvent(IntPtr hWnd)
        {
            try
            {
                IntPtr rootHWnd = NativeMethods.GetAncestor(hWnd, NativeMethods.GA_ROOT);
                if (rootHWnd == IntPtr.Zero) rootHWnd = hWnd;

                if (_taskbarHwnds.Contains(rootHWnd) || rootHWnd == _lastProcessedForegroundHwnd) return;
                _lastProcessedForegroundHwnd = rootHWnd;

                Guid desktopId = _desktopService.GetWindowDesktopId(rootHWnd);
                if (desktopId != Guid.Empty && desktopId != _desktopService.CurrentDesktopId)
                {
                    SwitchToDesktop(desktopId);
                }

                _pendingForegroundEvents++;
                IntPtr capturedHWnd = rootHWnd;
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        _pendingForegroundEvents--;
                        if (_pendingForegroundEvents > 0) return;

                        var item = Windows.FirstOrDefault(w => w.Handle == capturedHWnd);
                        if (item != null)
                        {
                            SetActiveWindowInColumn(item);
                        }
                        else if (ShouldManageWindow(capturedHWnd))
                        {
                            AddWindow(capturedHWnd);
                            DeferApplyLayout();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error in HandleForegroundEvent async dispatcher: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in HandleForegroundEvent: {ex.Message}");
            }
        }

        private void HandleWindowEvent(uint eventType, IntPtr hWnd)
        {
            try
            {
                switch (eventType)
                {
                    case NativeMethods.EVENT_OBJECT_CREATE:
                    case NativeMethods.EVENT_OBJECT_SHOW:
                        OnWindowCreatedOrShown(hWnd);
                        break;
                    case NativeMethods.EVENT_OBJECT_DESTROY:
                    case NativeMethods.EVENT_OBJECT_HIDE:
                        OnWindowDestroyedOrHidden(hWnd);
                        break;
                    case NativeMethods.EVENT_OBJECT_NAMECHANGE:
                        OnWindowTitleChanged(hWnd);
                        break;
                    case NativeMethods.EVENT_SYSTEM_MINIMIZESTART:
                        OnWindowMinimized(hWnd);
                        break;
                    case NativeMethods.EVENT_SYSTEM_MINIMIZEEND:
                        OnWindowRestored(hWnd);
                        break;
                    case NativeMethods.EVENT_OBJECT_UNCLOAKED:
                        OnWindowUncloaked(hWnd);
                        break;
                    case NativeMethods.EVENT_OBJECT_CLOAKED:
                        OnWindowCloaked(hWnd);
                        break;
                    case 0x000B: // EVENT_SYSTEM_MOVESIZEEND
                        OnWindowMovedOrResized(hWnd);
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in HandleWindowEvent for {eventType}: {ex.Message}");
            }
        }

        private void OnWindowCreatedOrShown(IntPtr hWnd)
        {
            try
            {
                if (!Windows.Any(w => w.Handle == hWnd) && ShouldManageWindow(hWnd))
                {
                    AddWindow(hWnd);
                    DeferApplyLayout();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OnWindowCreatedOrShown: {ex.Message}");
            }
        }

        private void OnWindowDestroyedOrHidden(IntPtr hWnd)
        {
            try
            {
                if (Windows.Any(w => w.Handle == hWnd))
                {
                    RemoveWindow(hWnd);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OnWindowDestroyedOrHidden: {ex.Message}");
            }
        }

        private void OnWindowTitleChanged(IntPtr hWnd)
        {
            try
            {
                IntPtr nameRoot = NativeMethods.GetAncestor(hWnd, NativeMethods.GA_ROOT);
                if (nameRoot == IntPtr.Zero) nameRoot = hWnd;

                if (Windows.Any(w => w.Handle == nameRoot))
                {
                    UpdateWindowTitle(nameRoot);
                }
                else if (ShouldManageWindow(nameRoot))
                {
                    AddWindow(nameRoot);
                    DeferApplyLayout();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OnWindowTitleChanged: {ex.Message}");
            }
        }

        private void OnWindowMinimized(IntPtr hWnd)
        {
            try
            {
                RemoveWindow(hWnd);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OnWindowMinimized: {ex.Message}");
            }
        }

        private void OnWindowRestored(IntPtr hWnd)
        {
            try
            {
                if (!Windows.Any(w => w.Handle == hWnd) && ShouldManageWindow(hWnd))
                {
                    AddWindow(hWnd);
                    DeferApplyLayout();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OnWindowRestored: {ex.Message}");
            }
        }

        private void OnWindowUncloaked(IntPtr hWnd)
        {
            try
            {
                IntPtr uncloakRoot = NativeMethods.GetAncestor(hWnd, NativeMethods.GA_ROOT);
                if (uncloakRoot == IntPtr.Zero) uncloakRoot = hWnd;
                if (!Windows.Any(w => w.Handle == uncloakRoot) && ShouldManageWindow(uncloakRoot))
                {
                    AddWindow(uncloakRoot);
                    DeferApplyLayout();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OnWindowUncloaked: {ex.Message}");
            }
        }

        private void OnWindowCloaked(IntPtr hWnd)
        {
            try
            {
                var item = Windows.FirstOrDefault(w => w.Handle == hWnd);
                if (item != null)
                {
                    if (item.IsActiveInColumn && _desktopService.IsWindowOnCurrentDesktop(hWnd))
                    {
                        RemoveWindow(hWnd);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OnWindowCloaked: {ex.Message}");
            }
        }

        private void OnWindowMovedOrResized(IntPtr hWnd)
        {
            try
            {
                var item = Windows.FirstOrDefault(w => w.Handle == hWnd);
                if (item != null)
                {
                    var screen = System.Windows.Forms.Screen.FromHandle(hWnd);
                    if (item.MonitorName != screen.DeviceName)
                    {
                        item.MonitorName = screen.DeviceName;
                        // Trigger layout recalculation since it crossed monitors
                        DeferApplyLayout();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OnWindowMovedOrResized: {ex.Message}");
            }
        }

        public void SetActiveWindowInColumn(WindowItem item)
        {
            int col = item.ColumnIndex;
            string key = $"{item.MonitorName}_{col}";
            _activeWindowsMap.TryGetValue(key, out var currentActive);
            if (currentActive != item)
            {
                _activeWindowsMap[key] = item;
                DeferApplyLayout();
            }

            // Bring to foreground
            if (NativeMethods.GetForegroundWindow() != item.Handle)
            {
                NativeMethods.SetForegroundWindow(item.Handle);
            }
        }

        public void MoveWindowToColumn(WindowItem item, int targetColumn)
        {
            if (!IsTileMode) return;
            if (targetColumn < 0 || targetColumn > 2) return;

            item.ColumnIndex = targetColumn;

            string key = $"{item.MonitorName}_{targetColumn}";
            _activeWindowsMap[key] = item;

            DeferApplyLayout();
        }

        public void EndSplit()
        {
            foreach (var item in Windows)
            {
                item.ColumnIndex = 0;
            }
            ColumnsCount = 1;
            DeferApplyLayout();
        }

        public void DeferApplyLayout()
        {
            if (_isLayoutPending) return;
            _isLayoutPending = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _isLayoutPending = false;
                UpdateActiveWindows();
                ApplyLayout();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        public void UpdateActiveWindows()
        {
            var activeWindows = Windows.Where(w => IsWindowOnCurrentDesktop(w.Handle)).ToList();

            // Collapse gaps between columns per monitor
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var monitorWindows = activeWindows.Where(w => w.MonitorName == screen.DeviceName).ToList();
                bool shifted;
                do
                {
                    shifted = false;
                    for (int i = 0; i < 2; i++)
                    {
                        bool currentHasWindows = monitorWindows.Any(w => w.ColumnIndex == i);
                        bool rightHasWindows = monitorWindows.Any(w => w.ColumnIndex > i);
                        if (!currentHasWindows && rightHasWindows)
                        {
                            foreach (var w in monitorWindows)
                            {
                                if (w.ColumnIndex > i)
                                {
                                    w.ColumnIndex--;
                                }
                            }
                            shifted = true;
                        }
                    }
                } while (shifted);
            }

            // ColumnsCount is updated dynamically per MainWindow in MainWindow.xaml.cs,
            // but we can still keep a global ColumnsCount fallback for other bindings if needed.
            int maxUsedColumn = 0;
            if (activeWindows.Count > 0)
            {
                maxUsedColumn = activeWindows.Max(w => w.ColumnIndex);
            }
            ColumnsCount = Math.Max(1, maxUsedColumn + 1);

            // Update active windows for all columns on each monitor
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var monitorWindows = activeWindows.Where(w => w.MonitorName == screen.DeviceName).ToList();
                for (int i = 0; i < 3; i++)
                {
                    var colWindows = monitorWindows.Where(w => w.ColumnIndex == i).ToList();
                    string key = $"{screen.DeviceName}_{i}";
                    if (colWindows.Count > 0)
                    {
                        _activeWindowsMap.TryGetValue(key, out var active);
                        if (active == null || active.ColumnIndex != i || !colWindows.Contains(active))
                        {
                            _activeWindowsMap[key] = colWindows.First();
                        }
                    }
                    else
                    {
                        _activeWindowsMap[key] = null;
                    }
                }
            }

            // Update IsActiveInColumn, CanMoveLeft, and CanMoveRight properties per monitor
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var monitorWindows = activeWindows.Where(w => w.MonitorName == screen.DeviceName).ToList();
                int col0Count = monitorWindows.Count(w => w.ColumnIndex == 0);
                int col1Count = monitorWindows.Count(w => w.ColumnIndex == 1);
                int col2Count = monitorWindows.Count(w => w.ColumnIndex == 2);
                int monitorColumnsCount = 1;
                if (monitorWindows.Count > 0)
                {
                    monitorColumnsCount = Math.Max(1, monitorWindows.Max(w => w.ColumnIndex) + 1);
                }

                foreach (var w in monitorWindows)
                {
                    string key = $"{screen.DeviceName}_{w.ColumnIndex}";
                    _activeWindowsMap.TryGetValue(key, out var active);
                    w.IsActiveInColumn = w.ColumnIndex >= 0 && w.ColumnIndex < 3 && w == active;

                    if (monitorColumnsCount > 1 && w.ColumnIndex == 0 && col0Count <= 1)
                    {
                        w.CanMoveRight = false;
                    }
                    else if (monitorColumnsCount == 2 && w.ColumnIndex == 1 && col1Count <= 1)
                    {
                        w.CanMoveRight = false;
                    }
                    else if (monitorColumnsCount == 3 && w.ColumnIndex == 1 && col1Count <= 1)
                    {
                        w.CanMoveRight = false;
                    }
                    else
                    {
                        w.CanMoveRight = true;
                    }

                    if (monitorColumnsCount == 3 && w.ColumnIndex == 1 && col1Count <= 1)
                    {
                        w.CanMoveLeft = false;
                    }
                    else
                    {
                        w.CanMoveLeft = true;
                    }
                }
            }
        }

        public void ApplyLayout()
        {
            if (!IsTileMode)
            {
                _layoutEngine.RestoreOriginalWindowPositions(Windows);
                return;
            }

            _layoutEngine.ApplyLayout(
                IsTileMode,
                Windows,
                _activeWindowsMap,
                IsWindowOnCurrentDesktop,
                GetSplitDirection
            );
        }

        private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var manager = (WindowManager)d;
            bool isTile = (bool)e.NewValue;
            if (!isTile)
            {
                manager.EndSplit();
            }
            manager.DeferApplyLayout();
        }

        public void MoveWindowToMonitor(WindowItem item, string targetMonitorName)
        {
            if (item.MonitorName == targetMonitorName) return;

            // Remove active status in the current column of the old monitor
            string oldKey = $"{item.MonitorName}_{item.ColumnIndex}";
            if (_activeWindowsMap.TryGetValue(oldKey, out var active) && active == item)
            {
                _activeWindowsMap[oldKey] = null;
            }

            // Change monitor and reset column index to 0
            item.MonitorName = targetMonitorName;
            item.ColumnIndex = 0;

            // Mark it as the active window in the target column of the new monitor
            string newKey = $"{targetMonitorName}_0";
            _activeWindowsMap[newKey] = item;

            // Update focused window in WindowManager
            NativeMethods.SetForegroundWindow(item.Handle);

            DeferApplyLayout();
        }

        public void MoveActiveWindowToAdjacentMonitor(bool goRight)
        {
            var activeHwnd = NativeMethods.GetForegroundWindow();
            if (activeHwnd == IntPtr.Zero) return;

            // Find the managed WindowItem
            var item = Windows.FirstOrDefault(w => w.Handle == activeHwnd);
            if (item == null) return;

            var screens = System.Windows.Forms.Screen.AllScreens.OrderBy(s => s.Bounds.X).ToList();
            var currentScreen = screens.FirstOrDefault(s => s.DeviceName == item.MonitorName);
            if (currentScreen == null) return;

            int currentIndex = screens.IndexOf(currentScreen);
            int targetIndex;

            if (goRight)
            {
                targetIndex = (currentIndex + 1) % screens.Count;
            }
            else
            {
                targetIndex = (currentIndex - 1 + screens.Count) % screens.Count;
            }

            if (targetIndex == currentIndex) return; // Only 1 monitor connected

            var targetScreen = screens[targetIndex];
            MoveWindowToMonitor(item, targetScreen.DeviceName);
        }

        private void RegisterGlobalHotkeys()
        {
            try
            {
                // Register Win + Shift + Left (ID: 1001)
                NativeMethods.RegisterHotKey(IntPtr.Zero, 1001, NativeMethods.MOD_WIN | NativeMethods.MOD_SHIFT, NativeMethods.VK_LEFT);
                // Register Win + Shift + Right (ID: 1002)
                NativeMethods.RegisterHotKey(IntPtr.Zero, 1002, NativeMethods.MOD_WIN | NativeMethods.MOD_SHIFT, NativeMethods.VK_RIGHT);

                System.Windows.Interop.ComponentDispatcher.ThreadFilterMessage += ComponentDispatcher_ThreadFilterMessage;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to register global hotkeys: {ex.Message}");
            }
        }

        private void UnregisterGlobalHotkeys()
        {
            try
            {
                System.Windows.Interop.ComponentDispatcher.ThreadFilterMessage -= ComponentDispatcher_ThreadFilterMessage;
                NativeMethods.UnregisterHotKey(IntPtr.Zero, 1001);
                NativeMethods.UnregisterHotKey(IntPtr.Zero, 1002);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to unregister global hotkeys: {ex.Message}");
            }
        }

        private void ComponentDispatcher_ThreadFilterMessage(ref System.Windows.Interop.MSG msg, ref bool handled)
        {
            if (msg.message == NativeMethods.WM_HOTKEY)
            {
                int id = msg.wParam.ToInt32();
                if (id == 1001) // Left
                {
                    MoveActiveWindowToAdjacentMonitor(false);
                    handled = true;
                }
                else if (id == 1002) // Right
                {
                    MoveActiveWindowToAdjacentMonitor(true);
                    handled = true;
                }
            }
        }
    }
}
