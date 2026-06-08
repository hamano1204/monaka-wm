using System;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace monaka_wm
{
    public partial class App : Application
    {
        private bool _isShuttingDown = false;

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            this.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var mainWindow = new MainWindow();
            mainWindow.Closed += (s, ev) =>
            {
                if (!_isShuttingDown)
                {
                    _isShuttingDown = true;
                    this.Shutdown();
                }
            };
            mainWindow.Show();

            WindowManager.Instance.Initialize();
        }

        private void Application_Exit(object sender, ExitEventArgs e)
        {
            _isShuttingDown = true;
            WindowManager.Instance.Shutdown();
        }
    }
}

