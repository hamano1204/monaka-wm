using System;
using System.ComponentModel;
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
        public MainWindow()
        {
            InitializeComponent();

            // Synchronously ensure handle and register to WindowManager to prevent race condition during startup scan
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            WindowManager.Instance.RegisterTaskbarHwnd(hwnd);

            DependencyPropertyDescriptor.FromProperty(WindowManager.IsTileModeProperty, typeof(WindowManager))
                .AddValueChanged(WindowManager.Instance, OnTileModeChanged);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Position at top of screen
            this.Left = 0;
            this.Top = 0;
            this.Width = SystemParameters.PrimaryScreenWidth;
            this.Height = WindowManager.TASKBAR_HEIGHT;

            // Register as AppBar
            AppBarHelper.Register(this, WindowManager.TASKBAR_HEIGHT);

            // Initialize ViewModel and set DataContext
            _viewModel = new MainViewModel();
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

            // Initialize NotifyIcon (System Tray)
            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true,
                Text = "monaka-wm"
            };

            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            contextMenu.Items.Add("monaka-wm の終了", null, (s, ev) => this.Close());
            _notifyIcon.ContextMenuStrip = contextMenu;

            // Initial selection sync
            SyncAllSelections();
        }

        private void Window_Closed(object sender, EventArgs e)
        {

            // Unregister AppBar (may throw if HwndSource is already disposed)
            try
            {
                AppBarHelper.Unregister(this);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AppBar unregister error: {ex.Message}");
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
        }

        private void WindowItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WindowItem.ColumnIndex) || e.PropertyName == nameof(WindowItem.IsActiveInColumn))
            {
                SyncAllSelections();
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

            int count = WindowManager.Instance.ColumnsCount;
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
                if (direction == "◀")
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
    }
}