using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace monaka_wm
{
    public static class AppBarHelper
    {
        private const int WM_WINDOWPOSCHANGING = 0x0046;

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        private class AppBarState
        {
            public Window Window { get; }
            public int TargetHeight { get; }
            public uint CallbackMessage { get; }
            public NativeMethods.RECT AppBarRect { get; set; }

            public AppBarState(Window window, int targetHeight, uint callbackMessage)
            {
                Window = window;
                TargetHeight = targetHeight;
                CallbackMessage = callbackMessage;
            }
        }

        private static readonly Dictionary<IntPtr, AppBarState> _states = new();

        public static void Register(Window window, int height)
        {
            WindowInteropHelper helper = new WindowInteropHelper(window);
            IntPtr hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero) return;

            if (_states.ContainsKey(hwnd)) return;

            // Generate a unique window message for each AppBar
            uint callbackMessage = NativeMethods.RegisterWindowMessage($"AppBarMessage_{hwnd.ToInt64()}");

            var state = new AppBarState(window, height, callbackMessage);
            _states[hwnd] = state;

            NativeMethods.APPBARDATA abd = new NativeMethods.APPBARDATA();
            abd.cbSize = Marshal.SizeOf(abd);
            abd.hWnd = hwnd;
            abd.uCallbackMessage = callbackMessage;

            NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref abd);

            SetPosition(hwnd, state);
            
            HwndSource source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(WndProc);
        }

        public static void Unregister(Window window)
        {
            WindowInteropHelper helper = new WindowInteropHelper(window);
            IntPtr hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero) return;

            if (!_states.TryGetValue(hwnd, out var state)) return;

            try
            {
                HwndSource? source = HwndSource.FromHwnd(hwnd);
                source?.RemoveHook(WndProc);
            }
            catch { }

            NativeMethods.APPBARDATA abd = new NativeMethods.APPBARDATA();
            abd.cbSize = Marshal.SizeOf(abd);
            abd.hWnd = hwnd;
            NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref abd);

            _states.Remove(hwnd);
        }

        private static void SetPosition(IntPtr hwnd, AppBarState state)
        {
            NativeMethods.APPBARDATA abd = new NativeMethods.APPBARDATA();
            abd.cbSize = Marshal.SizeOf(abd);
            abd.hWnd = hwnd;
            abd.uEdge = NativeMethods.ABE_TOP;

            var presentationSource = PresentationSource.FromVisual(state.Window);
            double dpiScaleY = 1.0;
            if (presentationSource?.CompositionTarget != null)
            {
                dpiScaleY = presentationSource.CompositionTarget.TransformToDevice.M22;
            }

            // Get target monitor bounds to position correctly in multi-monitor environment
            var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
            int screenLeft = screen.Bounds.Left;
            int screenWidthPixels = screen.Bounds.Width;
            int heightPixels = (int)(state.TargetHeight * dpiScaleY);

            // Setting appBar rect coordinates in physical pixels relative to the monitor
            abd.rc.Left = screenLeft;
            abd.rc.Top = screen.Bounds.Top;
            abd.rc.Right = screenLeft + screenWidthPixels;
            abd.rc.Bottom = screen.Bounds.Top + heightPixels;

            NativeMethods.SHAppBarMessage(NativeMethods.ABM_QUERYPOS, ref abd);
            abd.rc.Bottom = abd.rc.Top + heightPixels;

            NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETPOS, ref abd);

            state.AppBarRect = abd.rc;

            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, abd.rc.Left, abd.rc.Top, abd.rc.Right - abd.rc.Left, abd.rc.Bottom - abd.rc.Top, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (!_states.TryGetValue(hwnd, out var state)) return IntPtr.Zero;

            if (state.CallbackMessage != 0 && msg == state.CallbackMessage)
            {
                // ABN_POSCHANGED is wParam = 1
                if (wParam.ToInt32() == 1)
                {
                    SetPosition(hwnd, state);
                    handled = true;
                }
            }
            else if (msg == WM_WINDOWPOSCHANGING)
            {
                WINDOWPOS wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                
                // Force window to remain at the precise reserved screen coordinates
                wp.x = state.AppBarRect.Left;
                wp.y = state.AppBarRect.Top;
                wp.cx = state.AppBarRect.Right - state.AppBarRect.Left;
                wp.cy = state.AppBarRect.Bottom - state.AppBarRect.Top;
                
                // Remove flags that might prevent movement or size updates
                wp.flags &= ~(NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);
                
                Marshal.StructureToPtr(wp, lParam, true);
            }
            return IntPtr.Zero;
        }
    }
}
