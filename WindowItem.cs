using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace monaka_wm
{
    public class WindowItem : INotifyPropertyChanged
    {
        private string _title = string.Empty;
        private int _columnIndex = 0;
        private bool _isActiveInColumn = false;
        private string _monitorName = string.Empty;
        private bool _isOnCurrentDesktop = true;

        private bool _canMoveRight = true;

        public string MonitorName
        {
            get => _monitorName;
            set
            {
                if (_monitorName != value)
                {
                    _monitorName = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsOnCurrentDesktop
        {
            get => _isOnCurrentDesktop;
            set
            {
                if (_isOnCurrentDesktop != value)
                {
                    _isOnCurrentDesktop = value;
                    OnPropertyChanged();
                }
            }
        }

        public IntPtr Handle { get; }
        
        public NativeMethods.WINDOWPLACEMENT? OriginalPlacement { get; set; }
        
        public string Title
        {
            get => _title;
            set
            {
                if (_title != value)
                {
                    _title = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ProcessName { get; }

        public int ColumnIndex
        {
            get => _columnIndex;
            set
            {
                if (_columnIndex != value)
                {
                    _columnIndex = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsActiveInColumn
        {
            get => _isActiveInColumn;
            set
            {
                if (_isActiveInColumn != value)
                {
                    _isActiveInColumn = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _canMoveLeft = true;

        public bool CanMoveRight
        {
            get => _canMoveRight;
            set
            {
                if (_canMoveRight != value)
                {
                    _canMoveRight = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool CanMoveLeft
        {
            get => _canMoveLeft;
            set
            {
                if (_canMoveLeft != value)
                {
                    _canMoveLeft = value;
                    OnPropertyChanged();
                }
            }
        }

        public WindowItem(IntPtr handle, string title, string processName)
        {
            Handle = handle;
            Title = title;
            ProcessName = processName;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public override bool Equals(object? obj)
        {
            if (obj is WindowItem other)
            {
                return Handle == other.Handle;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return Handle.GetHashCode();
        }
    }
}
