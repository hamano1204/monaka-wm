using System;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace monaka_wm
{
    public partial class App : Application
    {
        private const string MutexName = "Global\\monaka-wm-single-instance";
        private Mutex? _mutex;

        private bool _isShuttingDown = false;
        private readonly System.Collections.Generic.List<MainWindow> _mainWindows = new();

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // 多重起動チェック
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                System.Windows.MessageBox.Show(
                    "monaka-wm はすでに起動しています。\nmonaka-wm is already running.",
                    "monaka-wm",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                _mutex.Close();
                _mutex = null;
                this.Shutdown();
                return;
            }

            this.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var screens = System.Windows.Forms.Screen.AllScreens;
            foreach (var screen in screens)
            {
                var window = new MainWindow(screen);
                window.Closed += (s, ev) =>
                {
                    if (!_isShuttingDown)
                    {
                        _isShuttingDown = true;
                        CloseAllWindows();
                        this.Shutdown();
                    }
                };
                _mainWindows.Add(window);
                window.Show();
            }

            WindowManager.Instance.Initialize();
        }

        private void CloseAllWindows()
        {
            foreach (var window in _mainWindows.ToArray())
            {
                try
                {
                    window.Close();
                }
                catch { }
            }
            _mainWindows.Clear();
        }

        private void Application_Exit(object sender, ExitEventArgs e)
        {
            _isShuttingDown = true;
            CloseAllWindows();
            WindowManager.Instance.Shutdown();

            // Mutexを解放
            if (_mutex != null)
            {
                try { _mutex.ReleaseMutex(); } catch { }
                _mutex.Close();
                _mutex = null;
            }
        }
    }
}

