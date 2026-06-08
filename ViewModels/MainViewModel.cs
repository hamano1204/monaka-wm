using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Application = System.Windows.Application;
using System.Windows.Data;
using System.Windows.Input;

namespace monaka_wm.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly ListCollectionView _column0Windows;
        private readonly ListCollectionView _column1Windows;
        private readonly ListCollectionView _column2Windows;
        private readonly System.Windows.Forms.Screen _screen;

        public ICollectionView Column0Windows => _column0Windows;
        public ICollectionView Column1Windows => _column1Windows;
        public ICollectionView Column2Windows => _column2Windows;

        public bool IsTileMode
        {
            get => WindowManager.Instance.IsTileMode;
            set
            {
                if (WindowManager.Instance.IsTileMode != value)
                {
                    WindowManager.Instance.IsTileMode = value;
                    OnPropertyChanged();
                }
            }
        }

        public int ColumnsCount => WindowManager.Instance.ColumnsCount;

        // Commands
        public ICommand MoveLeftCommand { get; }
        public ICommand MoveRightCommand { get; }
        public ICommand EndSplitCommand { get; }
        public ICommand SetActiveWindowCommand { get; }

        public MainViewModel() : this(System.Windows.Forms.Screen.PrimaryScreen!)
        {
        }

        public MainViewModel(System.Windows.Forms.Screen screen)
        {
            _screen = screen;

            // Create three independent views of the same collection, filtered by screen
            _column0Windows = new ListCollectionView(WindowManager.Instance.Windows)
            {
                Filter = item => item is WindowItem w && w.ColumnIndex == 0 && w.IsOnCurrentDesktop && w.MonitorName == _screen.DeviceName
            };

            _column1Windows = new ListCollectionView(WindowManager.Instance.Windows)
            {
                Filter = item => item is WindowItem w && w.ColumnIndex == 1 && w.IsOnCurrentDesktop && w.MonitorName == _screen.DeviceName
            };

            _column2Windows = new ListCollectionView(WindowManager.Instance.Windows)
            {
                Filter = item => item is WindowItem w && w.ColumnIndex == 2 && w.IsOnCurrentDesktop && w.MonitorName == _screen.DeviceName
            };

            // Hook commands
            MoveLeftCommand = new RelayCommand<WindowItem>(
                item => WindowManager.Instance.MoveWindowToColumn(item, item.ColumnIndex - 1),
                item => item != null && item.CanMoveLeft && IsTileMode
            );

            MoveRightCommand = new RelayCommand<WindowItem>(
                item => WindowManager.Instance.MoveWindowToColumn(item, item.ColumnIndex + 1),
                item => item != null && item.CanMoveRight && IsTileMode
            );

            EndSplitCommand = new RelayCommand(
                () => WindowManager.Instance.EndSplit(),
                () => IsTileMode && ColumnsCount > 1
            );

            SetActiveWindowCommand = new RelayCommand<WindowItem>(
                item => WindowManager.Instance.SetActiveWindowInColumn(item),
                item => item != null
            );

            // Listen to WindowManager property changes
            DependencyPropertyDescriptor.FromProperty(WindowManager.IsTileModeProperty, typeof(WindowManager))
                .AddValueChanged(WindowManager.Instance, (s, e) =>
                {
                    OnPropertyChanged(nameof(IsTileMode));
                    RefreshAllViews();
                });

            // ColumnsCount is a DependencyProperty on WindowManager. UI handles layout sync via descriptor.

            // Listen to Window collection changes
            WindowManager.Instance.Windows.CollectionChanged += (s, e) =>
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
                RefreshAllViews();
            };

            foreach (var w in WindowManager.Instance.Windows)
            {
                w.PropertyChanged += WindowItem_PropertyChanged;
            }

            WindowManager.Instance.DesktopChanged += (s, e) => RefreshAllViews();
        }

        private void WindowItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WindowItem.ColumnIndex) || 
                e.PropertyName == nameof(WindowItem.IsActiveInColumn) || 
                e.PropertyName == nameof(WindowItem.IsOnCurrentDesktop) ||
                e.PropertyName == nameof(WindowItem.MonitorName))
            {
                RefreshAllViews();
            }
        }

        public void RefreshAllViews()
        {
            // Safely refresh on UI thread
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                _column0Windows.Refresh();
                _column1Windows.Refresh();
                _column2Windows.Refresh();
            }));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
