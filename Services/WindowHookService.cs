using System;

namespace monaka_wm.Services
{
    public class WindowHookService
    {
        private IntPtr _foregroundHook = IntPtr.Zero;
        private IntPtr _objectLifecycleHook = IntPtr.Zero;
        private IntPtr _minimizeRestoreHook = IntPtr.Zero;
        private IntPtr _cloakHook = IntPtr.Zero;
        private IntPtr _moveSizeHook = IntPtr.Zero;
        
        private readonly NativeMethods.WinEventProc _winEventProc;
        private readonly Action<uint, IntPtr, int, int, uint, uint> _onWindowEvent;

        public WindowHookService(Action<uint, IntPtr, int, int, uint, uint> onWindowEvent)
        {
            _onWindowEvent = onWindowEvent ?? throw new ArgumentNullException(nameof(onWindowEvent));
            _winEventProc = WinEventCallback;
        }

        public void Start()
        {
            if (_foregroundHook != IntPtr.Zero) return;

            // Hook for foreground changes
            _foregroundHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _winEventProc,
                0,
                0,
                NativeMethods.WINEVENT_OUTOFCONTEXT
            );

            // Hook for window creation, destruction, show, hide, and name changes (range 0x8000 to 0x800C)
            _objectLifecycleHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_OBJECT_CREATE,
                NativeMethods.EVENT_OBJECT_NAMECHANGE,
                IntPtr.Zero,
                _winEventProc,
                0,
                0,
                NativeMethods.WINEVENT_OUTOFCONTEXT
            );

            // Hook for window minimize/restore
            _minimizeRestoreHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_MINIMIZESTART,
                NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
                IntPtr.Zero,
                _winEventProc,
                0,
                0,
                NativeMethods.WINEVENT_OUTOFCONTEXT
            );

            // Hook for window cloak/uncloak (critical for UWP apps like Calculator)
            _cloakHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_OBJECT_CLOAKED,
                NativeMethods.EVENT_OBJECT_UNCLOAKED,
                IntPtr.Zero,
                _winEventProc,
                0,
                0,
                NativeMethods.WINEVENT_OUTOFCONTEXT
            );

            // Hook for window move/size end (to detect monitor crossings)
            _moveSizeHook = NativeMethods.SetWinEventHook(
                0x000B, // EVENT_SYSTEM_MOVESIZEEND
                0x000B,
                IntPtr.Zero,
                _winEventProc,
                0,
                0,
                NativeMethods.WINEVENT_OUTOFCONTEXT
            );
        }

        public void Stop()
        {
            if (_foregroundHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
            }
            if (_objectLifecycleHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_objectLifecycleHook);
                _objectLifecycleHook = IntPtr.Zero;
            }
            if (_minimizeRestoreHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_minimizeRestoreHook);
                _minimizeRestoreHook = IntPtr.Zero;
            }
            if (_cloakHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_cloakHook);
                _cloakHook = IntPtr.Zero;
            }
            if (_moveSizeHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_moveSizeHook);
                _moveSizeHook = IntPtr.Zero;
            }
        }

        private void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            // Forward event to orchestrator
            _onWindowEvent(eventType, hWnd, idObject, idChild, dwEventThread, dwmsEventTime);
        }
    }
}
