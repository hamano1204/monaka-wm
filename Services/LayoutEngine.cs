using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using Application = System.Windows.Application;
using monaka_wm;

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
            IEnumerable<WindowItem> windows, 
            Dictionary<string, WindowItem?> activeWindowsMap, 
            Func<IntPtr, bool> isWindowOnCurrentDesktop,
            Func<string, SplitDirection> getSplitDirection)
        {
            if (_isApplyingLayout) return;
            _isApplyingLayout = true;

            try
            {
                // Capture original positions before applying Tile Mode layout
                foreach (var w in windows)
                {
                    CaptureWindowPlacement(w);
                }

                // Loop over all screens to apply independent layout per monitor
                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                {
                    // Find active windows belonging to this screen
                    var screenWindows = windows.Where(w => w.MonitorName == screen.DeviceName && 
                                                           NativeMethods.IsWindow(w.Handle) && 
                                                           isWindowOnCurrentDesktop(w.Handle)).ToList();

                    // Screen WorkingArea is already in physical pixels, so SetWindowPos coordinates match directly (no DPI conversion needed)
                    int layoutLeft = screen.WorkingArea.Left;
                    int layoutTop = screen.WorkingArea.Top;
                    int layoutWidth = screen.WorkingArea.Width;
                    int layoutHeight = screen.WorkingArea.Height;

                    if (WindowManager.Instance.IsPinned)
                    {
                        var (_, scaleY) = GetDpiScale();
                        int barHeightPhysical = (int)Math.Round(WindowManager.TASKBAR_HEIGHT * scaleY);
                        layoutTop += barHeightPhysical;
                        layoutHeight -= barHeightPhysical;
                    }

                    // Determine active windows for each column on this screen
                    var activeInCols = new List<WindowItem>();
                    for (int i = 0; i < 3; i++) // We support up to 3 columns
                    {
                        var colWindows = screenWindows.Where(w => w.ColumnIndex == i).ToList();
                        if (colWindows.Count > 0)
                        {
                            string key = $"{screen.DeviceName}_{i}";
                            activeWindowsMap.TryGetValue(key, out var active);
                            if (active != null && colWindows.Contains(active))
                            {
                                activeInCols.Add(active);
                            }
                            else
                            {
                                // Fallback to the first window in this column
                                var first = colWindows.First();
                                activeInCols.Add(first);
                            }
                        }
                    }

                    int activeColsCount = activeInCols.Count;

                    if (activeColsCount == 0)
                    {
                        // No active windows on this monitor, nothing to lay out
                        continue;
                    }

                    if (activeColsCount == 1)
                    {
                        // Single Column Layout on this monitor
                        var active = activeInCols[0];
                        EnsureWindowRestored(active.Handle);
                        int adjustedLeft = layoutLeft - BorderAdjustmentOffset;
                        int adjustedWidth = layoutWidth + (BorderAdjustmentOffset * 2);
                        int adjustedHeight = layoutHeight + BorderAdjustmentOffset;
                        NativeMethods.SetWindowPos(active.Handle, IntPtr.Zero, adjustedLeft, layoutTop, adjustedWidth, adjustedHeight, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW);

                        // Hide other windows on this monitor
                        foreach (var w in screenWindows)
                        {
                            if (w != active)
                            {
                                HideWindow(w.Handle);
                            }
                        }
                    }
                    else
                    {
                        var splitDir = getSplitDirection(screen.DeviceName);
                        bool isVertical = splitDir == SplitDirection.Vertical;

                        int splitTotalSize = isVertical ? layoutHeight : layoutWidth;
                        int nonSplitTotalSize = isVertical ? layoutWidth : layoutHeight;
                        int splitStart = isVertical ? layoutTop : layoutLeft;
                        int nonSplitStart = isVertical ? layoutLeft : layoutTop;

                        int stepSize = splitTotalSize / activeColsCount;

                        for (int i = 0; i < activeColsCount; i++)
                        {
                            var active = activeInCols[i];
                            int slotPos = splitStart + (i * stepSize);
                            int slotSize = (i == activeColsCount - 1) ? (splitTotalSize - (i * stepSize)) : stepSize;

                            EnsureWindowRestored(active.Handle);

                            int x, y, w, h;
                            if (isVertical)
                            {
                                x = nonSplitStart - BorderAdjustmentOffset;
                                y = slotPos;
                                w = nonSplitTotalSize + (BorderAdjustmentOffset * 2);
                                h = slotSize + BorderAdjustmentOffset;
                            }
                            else
                            {
                                x = slotPos - BorderAdjustmentOffset;
                                y = nonSplitStart;
                                w = slotSize + (BorderAdjustmentOffset * 2);
                                h = nonSplitTotalSize + BorderAdjustmentOffset;
                            }

                            NativeMethods.SetWindowPos(active.Handle, IntPtr.Zero, x, y, w, h, 
                                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW);
                        }

                        // Hide other windows on this monitor
                        foreach (var w in screenWindows)
                        {
                            if (!activeInCols.Contains(w))
                            {
                                HideWindow(w.Handle);
                            }
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
