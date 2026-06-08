using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using Application = System.Windows.Application;

namespace monaka_wm.Services
{
    public class LayoutEngine
    {
        private const int BorderAdjustmentOffset = 7; // Offset to compensate for Windows invisible resize borders/drop shadows
        private bool _isApplyingLayout = false;
        private readonly Dictionary<IntPtr, NativeMethods.WINDOWPLACEMENT> _originalPlacements = new();

        private (double scaleX, double scaleY) GetDpiScale()
        {
            double dpiScaleX = 1.0;
            double dpiScaleY = 1.0;
            if (Application.Current?.MainWindow != null)
            {
                var presentationSource = PresentationSource.FromVisual(Application.Current.MainWindow);
                if (presentationSource?.CompositionTarget != null)
                {
                    dpiScaleX = presentationSource.CompositionTarget.TransformToDevice.M11;
                    dpiScaleY = presentationSource.CompositionTarget.TransformToDevice.M22;
                }
            }
            return (dpiScaleX, dpiScaleY);
        }

        public void ApplyLayout(
            bool isTileMode, 
            int columnsCount, 
            IEnumerable<WindowItem> windows, 
            IList<WindowItem?> activeWindows, 
            Func<IntPtr, bool> isWindowOnCurrentDesktop)
        {
            if (!isTileMode)
            {
                RestoreOriginalWindowPositions(windows);
                return;
            }

            if (_isApplyingLayout) return;
            _isApplyingLayout = true;

            try
            {
                // Capture original positions before applying Tile Mode layout
                foreach (var w in windows)
                {
                    CaptureWindowPlacement(w);
                }

                // Tile Mode Layout Logic
                var (dpiScaleX, dpiScaleY) = GetDpiScale();

                // Use SystemParameters.WorkArea which automatically excludes both the Windows taskbar and our AppBar
                int layoutLeft = (int)(SystemParameters.WorkArea.Left * dpiScaleX);
                int layoutTop = (int)(SystemParameters.WorkArea.Top * dpiScaleY);
                int layoutWidth = (int)(SystemParameters.WorkArea.Width * dpiScaleX);
                int layoutHeight = (int)(SystemParameters.WorkArea.Height * dpiScaleY);

                int cols = columnsCount;

                // Determine active windows for currently active columns
                var activeInCols = new List<WindowItem>();
                for (int i = 0; i < cols; i++)
                {
                    if (i < activeWindows.Count)
                    {
                        var active = activeWindows[i];
                        if (active != null && NativeMethods.IsWindow(active.Handle) && isWindowOnCurrentDesktop(active.Handle))
                        {
                            activeInCols.Add(active);
                        }
                    }
                }

                int activeColsCount = activeInCols.Count;

                if (activeColsCount <= 1)
                {
                    // Single Column Layout (even if ColumnsCount > 1, if only one column has an active window, display it full width)
                    var active = activeInCols.FirstOrDefault();
                    if (active != null)
                    {
                        EnsureWindowRestored(active.Handle);
                        int adjustedLeft = layoutLeft - BorderAdjustmentOffset;
                        int adjustedWidth = layoutWidth + (BorderAdjustmentOffset * 2);
                        int adjustedHeight = layoutHeight + BorderAdjustmentOffset;
                        NativeMethods.SetWindowPos(active.Handle, IntPtr.Zero, adjustedLeft, layoutTop, adjustedWidth, adjustedHeight, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW);
                    }

                    // Hide all other windows
                    foreach (var w in windows)
                    {
                        if (w != active && NativeMethods.IsWindow(w.Handle) && isWindowOnCurrentDesktop(w.Handle))
                        {
                            HideWindow(w.Handle);
                        }
                    }
                }
                else
                {
                    // Multiple Columns Layout - evenly split based on activeColsCount
                    int colWidth = layoutWidth / activeColsCount;

                    for (int i = 0; i < activeColsCount; i++)
                    {
                        var active = activeInCols[i];
                        int colLeft = layoutLeft + (i * colWidth);
                        int colW = (i == activeColsCount - 1) ? (layoutWidth - (i * colWidth)) : colWidth;

                        EnsureWindowRestored(active.Handle);

                        int adjustedLeft = colLeft - BorderAdjustmentOffset;
                        int adjustedWidth = colW + (BorderAdjustmentOffset * 2);
                        int adjustedHeight = layoutHeight + BorderAdjustmentOffset;

                        NativeMethods.SetWindowPos(active.Handle, IntPtr.Zero, adjustedLeft, layoutTop, adjustedWidth, adjustedHeight, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW);
                    }

                    // Hide all other windows
                    foreach (var w in windows)
                    {
                        if (!activeInCols.Contains(w) && NativeMethods.IsWindow(w.Handle) && isWindowOnCurrentDesktop(w.Handle))
                        {
                            HideWindow(w.Handle);
                        }
                    }
                }
            }
            finally
            {
                _isApplyingLayout = false;
            }
        }

        public void EnsureWindowRestored(IntPtr hWnd)
        {
            if (!NativeMethods.IsWindow(hWnd)) return;

            bool isMinimized = NativeMethods.IsIconic(hWnd);
            int style = (int)NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_STYLE);
            bool isMaximized = (style & (int)NativeMethods.WS_MAXIMIZE) != 0;

            if (isMinimized || isMaximized)
            {
                var placement = new NativeMethods.WINDOWPLACEMENT();
                placement.length = Marshal.SizeOf(placement);
                if (NativeMethods.GetWindowPlacement(hWnd, ref placement))
                {
                    placement.showCmd = NativeMethods.SW_RESTORE;
                    NativeMethods.SetWindowPlacement(hWnd, ref placement);
                }
            }
        }

        public void HideWindow(IntPtr hWnd)
        {
            // Move off-screen but preserve the window's original size (SWP_NOSIZE).
            // Shrinking to 100x100 corrupts the window's remembered dimensions.
            NativeMethods.SetWindowPos(hWnd, IntPtr.Zero, -32000, -32000, 0, 0,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOSIZE);
        }

        public void RestoreAllWindows(IEnumerable<WindowItem> windows)
        {
            // Phase 1: Restore all windows that have a cached original position.
            // Use ShowWindow(SW_RESTORE) + SetWindowPos instead of SetWindowPlacement,
            // because SetWindowPlacement can silently fail for UWP/ApplicationFrameWindow
            // and for windows in other processes when they are off-screen.
            foreach (var kvp in _originalPlacements)
            {
                IntPtr hWnd = kvp.Key;
                if (!NativeMethods.IsWindow(hWnd)) continue;

                var rect = kvp.Value.rcNormalPosition;
                // Validate the cached rect is on-screen
                if (rect.Width <= 0 || rect.Height <= 0) continue;

                // If currently minimized, restore it first so SetWindowPos takes effect on normal placement
                if (NativeMethods.IsIconic(hWnd))
                {
                    NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);
                }

                // Explicitly reposition to original coordinates with SWP_SHOWWINDOW
                NativeMethods.SetWindowPos(
                    hWnd, IntPtr.Zero,
                    rect.Left, rect.Top, rect.Width, rect.Height,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW);
            }

            // Phase 2: Fallback for any managed windows that never had a cached position
            // (e.g. windows that were already off-screen when monaka-wm started).
            foreach (var w in windows)
            {
                if (!NativeMethods.IsWindow(w.Handle)) continue;
                if (_originalPlacements.ContainsKey(w.Handle)) continue;

                if (NativeMethods.IsIconic(w.Handle))
                {
                    NativeMethods.ShowWindow(w.Handle, NativeMethods.SW_RESTORE);
                }

                NativeMethods.SetWindowPos(w.Handle, IntPtr.Zero, 100, 100, 1024, 768,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW);
            }
        }


        private bool IsWindowHidden(WindowItem item, IEnumerable<WindowItem> windows)
        {
            return !item.IsActiveInColumn;
        }

        public void ClearCachedPlacement(IntPtr hWnd)
        {
            _originalPlacements.Remove(hWnd);
        }

        public void CaptureWindowPlacement(WindowItem item)
        {
            if (NativeMethods.IsWindow(item.Handle))
            {
                // Restore from cache if available
                if (_originalPlacements.TryGetValue(item.Handle, out var cachedPlacement))
                {
                    item.OriginalPlacement = cachedPlacement;
                    return;
                }

                if (item.OriginalPlacement == null)
                {
                    var placement = new NativeMethods.WINDOWPLACEMENT();
                    placement.length = Marshal.SizeOf(placement);
                    if (NativeMethods.GetWindowPlacement(item.Handle, ref placement))
                    {
                        // Avoid capturing offscreen coordinates (such as -32000) as the original position
                        if (placement.rcNormalPosition.Left <= -10000 || placement.rcNormalPosition.Top <= -10000)
                        {
                            return;
                        }
                        item.OriginalPlacement = placement;
                        _originalPlacements[item.Handle] = placement;
                    }
                }
            }
        }

        public void RestoreNormalPositionForWindow(IntPtr hWnd, WindowItem item)
        {
            if (!NativeMethods.IsWindow(hWnd)) return;

            NativeMethods.WINDOWPLACEMENT? placementToRestore = null;
            if (item.OriginalPlacement != null)
            {
                placementToRestore = item.OriginalPlacement;
            }
            else if (_originalPlacements.TryGetValue(hWnd, out var cached))
            {
                placementToRestore = cached;
            }

            if (placementToRestore != null)
            {
                var currentPlacement = new NativeMethods.WINDOWPLACEMENT();
                currentPlacement.length = Marshal.SizeOf(currentPlacement);
                if (NativeMethods.GetWindowPlacement(hWnd, ref currentPlacement))
                {
                    // Restore only the rcNormalPosition to avoid modifying showCmd
                    currentPlacement.rcNormalPosition = placementToRestore.Value.rcNormalPosition;
                    NativeMethods.SetWindowPlacement(hWnd, ref currentPlacement);
                }
            }
        }

        public void RestoreOriginalWindowPositions(IEnumerable<WindowItem> windows)
        {
            foreach (var w in windows)
            {
                NativeMethods.WINDOWPLACEMENT? placementToRestore = null;
                if (w.OriginalPlacement != null)
                {
                    placementToRestore = w.OriginalPlacement;
                }
                else if (_originalPlacements.TryGetValue(w.Handle, out var cached))
                {
                    placementToRestore = cached;
                }

                if (placementToRestore != null && NativeMethods.IsWindow(w.Handle))
                {
                    var placement = placementToRestore.Value;
                    NativeMethods.SetWindowPlacement(w.Handle, ref placement);
                    w.OriginalPlacement = null;
                }
            }
        }
    }
}
