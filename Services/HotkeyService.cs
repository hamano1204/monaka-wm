using System;
using System.Windows.Interop;

namespace monaka_wm.Services
{
    public class HotkeyService : IDisposable
    {
        private const int HOTKEY_LEFT_ID = 1001;
        private const int HOTKEY_RIGHT_ID = 1002;
        private bool _isRegistered = false;

        public event Action<bool>? HotkeyTriggered; // true: right, false: left

        public void Start()
        {
            if (_isRegistered) return;

            try
            {
                // Register Win + Shift + Left (ID: 1001)
                NativeMethods.RegisterHotKey(IntPtr.Zero, HOTKEY_LEFT_ID, NativeMethods.MOD_WIN | NativeMethods.MOD_SHIFT, NativeMethods.VK_LEFT);
                // Register Win + Shift + Right (ID: 1002)
                NativeMethods.RegisterHotKey(IntPtr.Zero, HOTKEY_RIGHT_ID, NativeMethods.MOD_WIN | NativeMethods.MOD_SHIFT, NativeMethods.VK_RIGHT);

                ComponentDispatcher.ThreadFilterMessage += ComponentDispatcher_ThreadFilterMessage;
                _isRegistered = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to register global hotkeys: {ex.Message}");
            }
        }

        public void Stop()
        {
            if (!_isRegistered) return;

            try
            {
                ComponentDispatcher.ThreadFilterMessage -= ComponentDispatcher_ThreadFilterMessage;
                NativeMethods.UnregisterHotKey(IntPtr.Zero, HOTKEY_LEFT_ID);
                NativeMethods.UnregisterHotKey(IntPtr.Zero, HOTKEY_RIGHT_ID);
                _isRegistered = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to unregister global hotkeys: {ex.Message}");
            }
        }

        private void ComponentDispatcher_ThreadFilterMessage(ref MSG msg, ref bool handled)
        {
            if (msg.message == NativeMethods.WM_HOTKEY)
            {
                int id = msg.wParam.ToInt32();
                if (id == HOTKEY_LEFT_ID)
                {
                    HotkeyTriggered?.Invoke(false);
                    handled = true;
                }
                else if (id == HOTKEY_RIGHT_ID)
                {
                    HotkeyTriggered?.Invoke(true);
                    handled = true;
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
