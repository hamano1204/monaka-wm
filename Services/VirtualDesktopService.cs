using System;
using System.Collections.Generic;
using monaka_wm;

namespace monaka_wm.Services
{
    public class DesktopLayoutState
    {
        public Dictionary<string, IntPtr> ActiveWindowHandles { get; } = new();
        public Dictionary<IntPtr, int> WindowColumns { get; } = new();
        public Dictionary<string, SplitDirection> MonitorSplitDirections { get; } = new();
    }

    public class VirtualDesktopService
    {
        private readonly IVirtualDesktopManager? _virtualDesktopManager;
        private readonly Dictionary<Guid, DesktopLayoutState> _desktopStates = new();
        private Guid _currentDesktopId = Guid.Empty;
        private bool _isComDisabled = false;

        public VirtualDesktopService()
        {
            try
            {
                _virtualDesktopManager = (IVirtualDesktopManager)new VirtualDesktopManager();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize VirtualDesktopManager COM: {ex.Message}");
                _isComDisabled = true;
            }
        }

        public Guid CurrentDesktopId
        {
            get => _currentDesktopId;
            set => _currentDesktopId = value;
        }

        public event EventHandler? DesktopChanged;

        public bool IsWindowOnCurrentDesktop(IntPtr hWnd)
        {
            if (_isComDisabled || _virtualDesktopManager == null) return true;
            if (_currentDesktopId == Guid.Empty) return true;
            try
            {
                if (_virtualDesktopManager.IsWindowOnCurrentVirtualDesktop(hWnd, out bool onCurrent) == 0)
                {
                    return onCurrent;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"IsWindowOnCurrentDesktop exception: {ex.Message}");
                _isComDisabled = true;
                return true;
            }
            return true;
        }

        public Guid GetWindowDesktopId(IntPtr hWnd)
        {
            if (_isComDisabled || _virtualDesktopManager == null) return Guid.Empty;
            try
            {
                if (_virtualDesktopManager.GetWindowDesktopId(hWnd, out Guid desktopId) == 0)
                {
                    return desktopId;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetWindowDesktopId exception: {ex.Message}");
                _isComDisabled = true;
            }
            return Guid.Empty;
        }

        public void MoveWindowToDesktop(IntPtr hWnd, ref Guid desktopId)
        {
            if (_isComDisabled || _virtualDesktopManager == null) return;
            try
            {
                _virtualDesktopManager.MoveWindowToDesktop(hWnd, ref desktopId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MoveWindowToDesktop exception: {ex.Message}");
                _isComDisabled = true;
            }
        }

        public DesktopLayoutState GetOrCreateState(Guid desktopId)
        {
            if (!_desktopStates.TryGetValue(desktopId, out var state))
            {
                state = new DesktopLayoutState();
                _desktopStates[desktopId] = state;
            }
            return state;
        }

        public void SaveState(Guid desktopId, Dictionary<string, WindowItem?> activeWindowsMap, IEnumerable<WindowItem> windows)
        {
            if (desktopId == Guid.Empty) return;

            var state = GetOrCreateState(desktopId);
            state.ActiveWindowHandles.Clear();
            foreach (var kvp in activeWindowsMap)
            {
                if (kvp.Value != null)
                {
                    state.ActiveWindowHandles[kvp.Key] = kvp.Value.Handle;
                }
            }

            state.WindowColumns.Clear();
            foreach (var w in windows)
            {
                state.WindowColumns[w.Handle] = w.ColumnIndex;
            }
        }

        public void RaiseDesktopChanged()
        {
            DesktopChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
