using System;
using System.Collections.Generic;

namespace monaka_wm.Services
{
    public class DesktopLayoutState
    {
        public int ColumnsCount { get; set; } = 1;
        public List<WindowItem?> ActiveWindows { get; } = new() { null, null, null };
        public Dictionary<IntPtr, int> WindowColumns { get; } = new();
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

        public void SaveState(Guid desktopId, int columnsCount, IList<WindowItem?> activeWindows, IEnumerable<WindowItem> windows)
        {
            if (desktopId == Guid.Empty) return;

            var state = GetOrCreateState(desktopId);
            state.ColumnsCount = columnsCount;
            
            for (int i = 0; i < 3; i++)
            {
                state.ActiveWindows[i] = i < activeWindows.Count ? activeWindows[i] : null;
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
