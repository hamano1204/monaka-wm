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
    public class WindowManager : DependencyObject
    {
        private static WindowManager? _instance;
        public static WindowManager Instance => _instance ??= new WindowManager();

        private readonly WindowHookService _hookService;
        private readonly VirtualDesktopService _desktopService;
        private readonly LayoutEngine _layoutEngine;

        private IntPtr _taskbarHwnd = IntPtr.Zero;
        public const int TASKBAR_HEIGHT = 45; // Taskbar height in WPF units

        public ObservableCollection<WindowItem> Windows { get; } = new();

        private IntPtr _lastProcessedForegroundHwnd = IntPtr.Zero;
        private bool _isLayoutPending = false;

        private readonly List<WindowItem?> _activeWindows = new() { null, null, null };

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
                    _desktopService.SaveState(_desktopService.CurrentDesktopId, ColumnsCount, _activeWindows, Windows);
                }

                // 2. Update current desktop ID
                _desktopService.CurrentDesktopId = newDesktopId;

                // Move our WPF bar window to the new virtual desktop so it remains visible
                if (_taskbarHwnd != IntPtr.Zero)
                {
                    _desktopService.MoveWindowToDesktop(_taskbarHwnd, ref newDesktopId);
                }

                // 3. Load or create new desktop state
                var state = _desktopService.GetOrCreateState(newDesktopId);

                // 4. Restore ColumnsCount
                ColumnsCount = state.ColumnsCount;

                // 5. Restore ColumnIndex for managed windows on the new desktop
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

                // 6. Restore active windows
                for (int i = 0; i < 3; i++)
                {
                    var active = i < state.ActiveWindows.Count ? state.ActiveWindows[i] : null;
                    if (active != null && Windows.Contains(active))
                    {
                        if (_desktopService.IsWindowOnCurrentDesktop(active.Handle))
                        {
                            _activeWindows[i] = active;
                            continue;
                        }
                    }
                    _activeWindows[i] = null;
                }

                // 7. Notify UI and apply layout
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
        }

        public void RegisterTaskbarHwnd(IntPtr hWnd)
        {
            _taskbarHwnd = hWnd;
        }

        public void Shutdown()
        {
            _hookService.Stop();

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
            if (hWnd == IntPtr.Zero || hWnd == _taskbarHwnd) return false;
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

            var item = new WindowItem(hWnd, title.ToString(), processName)
            {
                ColumnIndex = 0,
                IsOnCurrentDesktop = IsWindowOnCurrentDesktop(hWnd)
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
                
                // If it was the active window, update active
                if (item.ColumnIndex >= 0 && item.ColumnIndex < 3 && _activeWindows[item.ColumnIndex] == item)
                {
                    _activeWindows[item.ColumnIndex] = null;
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

                if (rootHWnd == _taskbarHwnd || rootHWnd == _lastProcessedForegroundHwnd) return;
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

        public void SetActiveWindowInColumn(WindowItem item)
        {
            int col = item.ColumnIndex;
            var currentActive = _activeWindows[col];
            if (currentActive != item)
            {
                _activeWindows[col] = item;
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

            int maxUsedColumn = 0;
            if (Windows.Count > 0)
            {
                maxUsedColumn = Windows.Max(w => w.ColumnIndex);
            }
            ColumnsCount = Math.Max(1, maxUsedColumn + 1);

            _activeWindows[targetColumn] = item;

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

            // Collapse gaps between columns
            bool shifted;
            do
            {
                shifted = false;
                for (int i = 0; i < 2; i++)
                {
                    bool currentHasWindows = activeWindows.Any(w => w.ColumnIndex == i);
                    bool rightHasWindows = activeWindows.Any(w => w.ColumnIndex > i);
                    if (!currentHasWindows && rightHasWindows)
                    {
                        foreach (var w in activeWindows)
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

            // Recalculate ColumnsCount
            int maxUsedColumn = 0;
            if (activeWindows.Count > 0)
            {
                maxUsedColumn = activeWindows.Max(w => w.ColumnIndex);
            }
            ColumnsCount = Math.Max(1, maxUsedColumn + 1);

            // Update active windows for all 3 columns
            for (int i = 0; i < 3; i++)
            {
                var colWindows = activeWindows.Where(w => w.ColumnIndex == i).ToList();
                if (colWindows.Count > 0)
                {
                    var active = _activeWindows[i];
                    if (active == null || active.ColumnIndex != i || !colWindows.Contains(active))
                    {
                        _activeWindows[i] = colWindows.First();
                    }
                }
                else
                {
                    _activeWindows[i] = null;
                }
            }

            // Update IsActiveInColumn, CanMoveLeft, and CanMoveRight properties
            int col0Count = activeWindows.Count(w => w.ColumnIndex == 0);
            int col1Count = activeWindows.Count(w => w.ColumnIndex == 1);
            int col2Count = activeWindows.Count(w => w.ColumnIndex == 2);

            foreach (var w in activeWindows)
            {
                w.IsActiveInColumn = w.ColumnIndex >= 0 && w.ColumnIndex < 3 && w == _activeWindows[w.ColumnIndex];
                
                if (ColumnsCount > 1 && w.ColumnIndex == 0 && col0Count <= 1)
                {
                    w.CanMoveRight = false;
                }
                else if (ColumnsCount == 2 && w.ColumnIndex == 1 && col1Count <= 1)
                {
                    w.CanMoveRight = false;
                }
                else if (ColumnsCount == 3 && w.ColumnIndex == 1 && col1Count <= 1)
                {
                    w.CanMoveRight = false;
                }
                else
                {
                    w.CanMoveRight = true;
                }

                if (ColumnsCount == 3 && w.ColumnIndex == 1 && col1Count <= 1)
                {
                    w.CanMoveLeft = false;
                }
                else
                {
                    w.CanMoveLeft = true;
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
                ColumnsCount,
                Windows,
                _activeWindows,
                IsWindowOnCurrentDesktop
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
    }
}
