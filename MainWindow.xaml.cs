using System;
using System.ComponentModel;
using System.Linq;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using monaka_wm.ViewModels;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;

namespace monaka_wm
{
    public partial class MainWindow : Window
    {
        private MainViewModel? _viewModel;
        private bool _isUpdatingLayout = false;
        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private bool _isSyncingSelection = false;
        private readonly System.Windows.Forms.Screen _targetScreen;
        private System.Windows.Threading.DispatcherTimer? _hoverTimer;

        public MainWindow() : this(System.Windows.Forms.Screen.PrimaryScreen!)
        {
        }

        public MainWindow(System.Windows.Forms.Screen screen)
        {
            _targetScreen = screen;
            InitializeComponent();

            // Synchronously ensure handle and register to WindowManager to prevent race condition during startup scan
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            WindowManager.Instance.RegisterTaskbarHwnd(hwnd);

            DependencyPropertyDescriptor.FromProperty(WindowManager.IsTileModeProperty, typeof(WindowManager))
                .AddValueChanged(WindowManager.Instance, OnTileModeChanged);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Position at top of the target screen
            var presentationSource = PresentationSource.FromVisual(this);
            double dpiScaleX = 1.0;
            double dpiScaleY = 1.0;
            if (presentationSource?.CompositionTarget != null)
            {
                dpiScaleX = presentationSource.CompositionTarget.TransformToDevice.M11;
                dpiScaleY = presentationSource.CompositionTarget.TransformToDevice.M22;
            }

            this.Left = _targetScreen.Bounds.Left / dpiScaleX;
            this.Top = _targetScreen.Bounds.Top / dpiScaleY;
            this.Width = _targetScreen.Bounds.Width / dpiScaleX;
            this.Height = 4; // Start collapsed

            // Apply WS_EX_NOACTIVATE so clicking tabs doesn't steal window focus
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = (int)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle | (int)NativeMethods.WS_EX_NOACTIVATE));

            // Initialize ViewModel and set DataContext, filtering for this screen
            _viewModel = new MainViewModel(_targetScreen);
            this.DataContext = _viewModel;

            // Listen to Collection changes to attach/detach property changed handlers for selection sync
            WindowManager.Instance.Windows.CollectionChanged += Windows_CollectionChanged;
            foreach (var w in WindowManager.Instance.Windows)
            {
                w.PropertyChanged += WindowItem_PropertyChanged;
            }

            // Sync visual layout
            SyncLayoutDefinitions();

            // Listen to virtual desktop switches
            WindowManager.Instance.DesktopChanged += OnDesktopChanged;

            // Listen to SplitDirectionChanged in WindowManager to update layout definitions
            WindowManager.Instance.SplitDirectionChanged += OnSplitDirectionChanged;

            // Initialize NotifyIcon (System Tray) - Only on primary screen to avoid duplicates
            if (_targetScreen.Primary)
            {
                _notifyIcon = new System.Windows.Forms.NotifyIcon
                {
                    Icon = System.Drawing.SystemIcons.Application,
                    Visible = true,
                    Text = "monaka-wm"
                };

                var contextMenu = new System.Windows.Forms.ContextMenuStrip();
                var startupItem = new System.Windows.Forms.ToolStripMenuItem("PC起動時に自動起動する")
                {
                    CheckOnClick = true,
                    Checked = IsStartupEnabled()
                };
                startupItem.CheckedChanged += (s, ev) =>
                {
                    SetStartup(startupItem.Checked);
                };
                contextMenu.Items.Add(startupItem);
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                contextMenu.Items.Add("monaka-wm の終了", null, (s, ev) => this.Close());
                _notifyIcon.ContextMenuStrip = contextMenu;
            }

            // Initial selection sync
            SyncAllSelections();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            if (_hoverTimer != null)
            {
                _hoverTimer.Stop();
                _hoverTimer = null;
            }

            // Remove DependencyPropertyDescriptor listeners
            try
            {
                DependencyPropertyDescriptor.FromProperty(WindowManager.IsTileModeProperty, typeof(WindowManager))
                    .RemoveValueChanged(WindowManager.Instance, OnTileModeChanged);
            }
            catch { }

            // Unsubscribe DesktopChanged
            WindowManager.Instance.DesktopChanged -= OnDesktopChanged;

            // Unsubscribe SplitDirectionChanged
            WindowManager.Instance.SplitDirectionChanged -= OnSplitDirectionChanged;

            // Unsubscribe from WindowManager events to prevent memory leaks
            WindowManager.Instance.Windows.CollectionChanged -= Windows_CollectionChanged;
            foreach (var w in WindowManager.Instance.Windows)
            {
                w.PropertyChanged -= WindowItem_PropertyChanged;
            }

            // Dispose NotifyIcon
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
        }

        private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_hoverTimer != null)
            {
                _hoverTimer.Stop();
            }

            _hoverTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _hoverTimer.Tick += (s, ev) =>
            {
                _hoverTimer.Stop();
                ExpandWindow();
            };
            _hoverTimer.Start();
        }

        private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_hoverTimer != null)
            {
                _hoverTimer.Stop();
            }

            CollapseWindow();
        }

        private void ExpandWindow()
        {
            this.Height = WindowManager.TASKBAR_HEIGHT;
            MainContentGrid.Visibility = Visibility.Visible;
        }

        private void CollapseWindow()
        {
            this.Height = 4;
            MainContentGrid.Visibility = Visibility.Collapsed;
        }

        private void Windows_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (WindowItem item in e.OldItems)
                {
                    item.PropertyChanged -= WindowItem_PropertyChanged;
                }
            }
            if (e.NewItems != null)
            {
                foreach (WindowItem item in e.NewItems)
                {
                    item.PropertyChanged += WindowItem_PropertyChanged;
                }
            }
            SyncAllSelections();
            SyncLayoutDefinitions();
        }

        private void WindowItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WindowItem.ColumnIndex) || 
                e.PropertyName == nameof(WindowItem.IsActiveInColumn) || 
                e.PropertyName == nameof(WindowItem.MonitorName))
            {
                SyncAllSelections();
                SyncLayoutDefinitions();
            }
        }

        private void SyncAllSelections()
        {
            Dispatcher.BeginInvoke(() =>
            {
                SyncSelection(Column0ListBox, 0);
                SyncSelection(Column1ListBox, 1);
                SyncSelection(Column2ListBox, 2);
            });
        }

        private void SyncSelection(ListBox listBox, int colIndex)
        {
            // Find active item in column
            foreach (var item in listBox.Items)
            {
                if (item is WindowItem w && w.IsActiveInColumn && w.ColumnIndex == colIndex)
                {
                    if (listBox.SelectedItem != w)
                    {
                        _isSyncingSelection = true;
                        try
                        {
                            listBox.SelectedItem = w;
                        }
                        finally
                        {
                            _isSyncingSelection = false;
                        }
                    }
                    return;
                }
            }
        }

        private void OnDesktopChanged(object? sender, EventArgs e)
        {
            SyncAllSelections();
        }

        private void OnSplitDirectionChanged(string monitorName, SplitDirection dir)
        {
            if (monitorName == _targetScreen.DeviceName)
            {
                SyncLayoutDefinitions();
            }
        }

        private void BurgerMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                btn.ContextMenu.IsOpen = true;
            }
        }

        private void ExitMenu_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Application.Current.Shutdown();
        }

        private void TabListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingSelection) return;

            if (sender is ListBox listBox && listBox.SelectedItem is WindowItem item)
            {
                _viewModel?.SetActiveWindowCommand.Execute(item);
            }
        }

        private void OnTileModeChanged(object? sender, EventArgs e)
        {
            SyncLayoutDefinitions();
        }

        private void SyncLayoutDefinitions()
        {
            if (_isUpdatingLayout) return;
            _isUpdatingLayout = true;

            int count = 1;
            if (_targetScreen != null)
            {
                var monitorWindows = WindowManager.Instance.Windows
                    .Where(w => w.MonitorName == _targetScreen.DeviceName && WindowManager.Instance.IsWindowOnCurrentDesktop(w.Handle))
                    .ToList();
                if (monitorWindows.Count > 0)
                {
                    count = Math.Max(1, monitorWindows.Max(w => w.ColumnIndex) + 1);
                }
            }

            bool isTile = WindowManager.Instance.IsTileMode;

            if (!isTile || count <= 1)
            {
                Column0Definition.Width = new GridLength(1, GridUnitType.Star);
                Splitter0Definition.Width = new GridLength(0);
                Column1Definition.Width = new GridLength(0);
                Splitter1Definition.Width = new GridLength(0);
                Column2Definition.Width = new GridLength(0);

                Splitter0Control.Visibility = Visibility.Collapsed;
                Column1ListBox.Visibility = Visibility.Collapsed;
                Splitter1Control.Visibility = Visibility.Collapsed;
                Column2ListBox.Visibility = Visibility.Collapsed;
                EndSplitButton.Visibility = Visibility.Collapsed;
            }
            else if (count == 2)
            {
                Column0Definition.Width = new GridLength(0.5, GridUnitType.Star);
                Splitter0Definition.Width = new GridLength(1);
                Column1Definition.Width = new GridLength(0.5, GridUnitType.Star);
                Splitter1Definition.Width = new GridLength(0);
                Column2Definition.Width = new GridLength(0);

                Splitter0Control.Visibility = Visibility.Visible;
                Column1ListBox.Visibility = Visibility.Visible;
                Splitter1Control.Visibility = Visibility.Collapsed;
                Column2ListBox.Visibility = Visibility.Collapsed;
                EndSplitButton.Visibility = Visibility.Visible;
            }
            else // count >= 3
            {
                Column0Definition.Width = new GridLength(1.0 / 3.0, GridUnitType.Star);
                Splitter0Definition.Width = new GridLength(1);
                Column1Definition.Width = new GridLength(1.0 / 3.0, GridUnitType.Star);
                Splitter1Definition.Width = new GridLength(1);
                Column2Definition.Width = new GridLength(1.0 / 3.0, GridUnitType.Star);

                Splitter0Control.Visibility = Visibility.Visible;
                Column1ListBox.Visibility = Visibility.Visible;
                Splitter1Control.Visibility = Visibility.Visible;
                Column2ListBox.Visibility = Visibility.Visible;
                EndSplitButton.Visibility = Visibility.Visible;
            }

            _isUpdatingLayout = false;
        }

        private void TabListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is ListBox listBox)
            {
                var scrollViewer = GetScrollViewer(listBox);
                if (scrollViewer != null)
                {
                    double newOffset = scrollViewer.HorizontalOffset - e.Delta;
                    scrollViewer.ScrollToHorizontalOffset(Math.Clamp(newOffset, 0, scrollViewer.ScrollableWidth));
                    e.Handled = true;
                }
            }
        }

        private ScrollViewer? GetScrollViewer(DependencyObject depObj)
        {
            if (depObj is ScrollViewer) return (ScrollViewer)depObj;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
            {
                var child = VisualTreeHelper.GetChild(depObj, i);
                var result = GetScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }

        private void ArrowButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is WindowItem windowItem)
            {
                var direction = btn.Content.ToString();
                if (direction == "◀" || direction == "▲")
                {
                    WindowManager.Instance.MoveWindowToColumn(windowItem, windowItem.ColumnIndex - 1);
                }
                else
                {
                    WindowManager.Instance.MoveWindowToColumn(windowItem, windowItem.ColumnIndex + 1);
                }
            }
            e.Handled = true;
        }

        private void TabContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.ContextMenu menu)
            {
                menu.Items.Clear();

                if (menu.PlacementTarget is FrameworkElement element && element.DataContext is WindowItem item)
                {
                    var screens = System.Windows.Forms.Screen.AllScreens.OrderBy(s => s.Bounds.X).ToList();
                    var menuItemStyle = this.FindResource("TabMenuItemStyle") as Style;

                    foreach (var screen in screens)
                    {
                        string screenName = screen.Primary 
                            ? $"{screen.DeviceName} (メイン モニター)" 
                            : $"{screen.DeviceName}";

                        var menuItem = new System.Windows.Controls.MenuItem
                        {
                            Header = screenName,
                            IsChecked = (item.MonitorName == screen.DeviceName),
                            IsEnabled = (item.MonitorName != screen.DeviceName),
                            Style = menuItemStyle
                        };

                        menuItem.Click += (s, ev) =>
                        {
                            WindowManager.Instance.MoveWindowToMonitor(item, screen.DeviceName);
                        };

                        menu.Items.Add(menuItem);
                    }
                }
            }
        }

        private const string StartupRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "monaka-wm";

        private bool IsStartupEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey);
                if (key != null)
                {
                    var val = key.GetValue(StartupValueName);
                    return val != null && val.ToString() == Environment.ProcessPath;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to read registry: {ex.Message}");
            }
            return false;
        }

        private void SetStartup(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, true);
                if (key != null)
                {
                    if (enable)
                    {
                        var processPath = Environment.ProcessPath;
                        if (!string.IsNullOrEmpty(processPath))
                        {
                            key.SetValue(StartupValueName, processPath);
                        }
                    }
                    else
                    {
                        key.DeleteValue(StartupValueName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to write registry: {ex.Message}");
            }
        }
    }
}