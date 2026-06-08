using System;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace monaka_wm
{
    public partial class App : Application
    {
        private bool _isShuttingDown = false;
        private readonly System.Collections.Generic.List<MainWindow> _mainWindows = new();

        private void Application_Startup(object sender, StartupEventArgs e)
        {
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
        }
    }
}

