using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace monaka_wm
{
    public class WindowItem : INotifyPropertyChanged
    {
        // プロセス名ベースのアイコンキャッシュ（同一アプリの重複取得を防止）
        private static readonly ConcurrentDictionary<string, System.Windows.Media.ImageSource?> _iconCache = new();

        private string _title = string.Empty;
        private int _columnIndex = 0;
        private bool _isActiveInColumn = false;
        private string _monitorName = string.Empty;
        private bool _isOnCurrentDesktop = true;
        private System.Windows.Media.ImageSource? _icon;

        public System.Windows.Media.ImageSource? Icon
        {
            get => _icon;
            set
            {
                if (_icon != value)
                {
                    _icon = value;
                    OnPropertyChanged();
                }
            }
        }

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

        private bool _canMoveRight = true;
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
            LoadIconAsync();
        }

        public void LoadIconAsync()
        {
            // キャッシュに既にある場合は即座に適用
            if (_iconCache.TryGetValue(ProcessName, out var cached))
            {
                Icon = cached;
                return;
            }

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var img = ExtractWindowIcon(Handle);
                    // プロセス名でキャッシュ（nullでも記録して再試行を防止）
                    _iconCache.TryAdd(ProcessName, img);
                    if (img != null)
                    {
                        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                        {
                            Icon = img;
                        }));
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error loading icon: {ex.Message}");
                }
            });
        }

        private System.Windows.Media.ImageSource? ExtractWindowIcon(IntPtr hWnd)
        {
            // --- 高速パス: 実行ファイルから直接アイコン取得（WM_GETICONを使わないので応答待ちなし）---
            try
            {
                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                string? exePath = proc.MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                    if (icon != null)
                    {
                        var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                            icon.Handle,
                            System.Windows.Int32Rect.Empty,
                            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                        src.Freeze();
                        return src;
                    }
                }
            }
            catch { }

            // --- フォールバック: WM_GETICON（タイムアウトを20msに短縮）---
            IntPtr hIcon = IntPtr.Zero;

            const uint WM_GETICON = 0x007F;
            const IntPtr ICON_SMALL2 = 2;
            const IntPtr ICON_SMALL = 0;
            const IntPtr ICON_BIG = 1;
            // SMTO_ABORTIFHUNG | SMTO_NOTIMEOUTIFNOTHUNG: ハングしている場合のみ即abort、正常時は応答まで待つ
            const uint SMTO_ABORTIFHUNG = 0x0002;

            IntPtr result;
            if (NativeMethods.SendMessageTimeout(hWnd, WM_GETICON, ICON_SMALL2, IntPtr.Zero, SMTO_ABORTIFHUNG, 20, out result) != IntPtr.Zero && result != IntPtr.Zero)
            {
                hIcon = result;
            }
            else if (NativeMethods.SendMessageTimeout(hWnd, WM_GETICON, ICON_SMALL, IntPtr.Zero, SMTO_ABORTIFHUNG, 20, out result) != IntPtr.Zero && result != IntPtr.Zero)
            {
                hIcon = result;
            }
            else if (NativeMethods.SendMessageTimeout(hWnd, WM_GETICON, ICON_BIG, IntPtr.Zero, SMTO_ABORTIFHUNG, 20, out result) != IntPtr.Zero && result != IntPtr.Zero)
            {
                hIcon = result;
            }

            if (hIcon == IntPtr.Zero)
            {
                const int GCLP_HICONSM = -34;
                const int GCLP_HICON = -14;
                
                try
                {
                    if (IntPtr.Size == 8)
                    {
                        hIcon = NativeMethods.GetClassLongPtr64(hWnd, GCLP_HICONSM);
                        if (hIcon == IntPtr.Zero)
                        {
                            hIcon = NativeMethods.GetClassLongPtr64(hWnd, GCLP_HICON);
                        }
                    }
                    else
                    {
                        hIcon = new IntPtr(NativeMethods.GetClassLong32(hWnd, GCLP_HICONSM));
                        if (hIcon == IntPtr.Zero)
                        {
                            hIcon = new IntPtr(NativeMethods.GetClassLong32(hWnd, GCLP_HICON));
                        }
                    }
                }
                catch { }
            }

            if (hIcon != IntPtr.Zero)
            {
                try
                {
                    var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        hIcon,
                        System.Windows.Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    
                    bitmapSource.Freeze();
                    return bitmapSource;
                }
                catch
                {
                    return GetFallbackIcon();
                }
                finally
                {
                    NativeMethods.DestroyIcon(hIcon);
                }
            }

            return GetFallbackIcon();
        }

        private System.Windows.Media.ImageSource? GetFallbackIcon()
        {
            try
            {
                var systemIcon = System.Drawing.SystemIcons.Application;
                var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    systemIcon.Handle,
                    System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                bitmapSource.Freeze();
                return bitmapSource;
            }
            catch
            {
                return null;
            }
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
