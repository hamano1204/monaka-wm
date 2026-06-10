using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
            Icon = GetFallbackIcon();
            LoadIconAsync();
        }

        public void LoadIconAsync()
        {
            string cacheKey = GetIconCacheKey();

            // キャッシュに既にある場合は即座に適用
            if (_iconCache.TryGetValue(cacheKey, out var cached))
            {
                Icon = cached;
                return;
            }

            // まずはプレースホルダーを表示し、バックグラウンドで実際のアイコンを取得する
            Icon = GetFallbackIcon();

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var img = ExtractWindowIcon(Handle) ?? GetFallbackIcon();
                    _iconCache.TryAdd(cacheKey, img);
                    System.Windows.Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        Icon = img;
                    }));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error loading icon: {ex.Message}");
                }
            });
        }

        private string GetIconCacheKey()
        {
            return $"{ProcessName}:{Handle.ToInt64()}";
        }

        private System.Windows.Media.ImageSource? ExtractWindowIcon(IntPtr hWnd)
        {
            var windowIcon = GetWindowIcon(hWnd);
            if (windowIcon != null)
            {
                return windowIcon;
            }

            var childIcon = GetWindowIconFromChildWindows(hWnd);
            if (childIcon != null)
            {
                return childIcon;
            }

            var shellIcon = GetShellAppIcon(hWnd);
            if (shellIcon != null)
            {
                return shellIcon;
            }

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

            return null;
        }

        private System.Windows.Media.ImageSource? GetWindowIcon(IntPtr hWnd)
        {
            IntPtr hIcon = IntPtr.Zero;

            const uint WM_GETICON = 0x007F;
            var ICON_SMALL2 = new IntPtr(2);
            var ICON_SMALL = new IntPtr(0);
            var ICON_BIG = new IntPtr(1);
            const uint SMTO_ABORTIFHUNG = 0x0002;

            if (NativeMethods.SendMessageTimeout(hWnd, WM_GETICON, ICON_SMALL2, IntPtr.Zero, SMTO_ABORTIFHUNG, 20, out var result) != IntPtr.Zero && result != IntPtr.Zero)
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
                    return null;
                }
            }

            return null;
        }

        private System.Windows.Media.ImageSource? GetWindowIconFromChildWindows(IntPtr hWnd)
        {
            System.Windows.Media.ImageSource? result = null;
            NativeMethods.EnumChildWindows(hWnd, (child, lparam) =>
            {
                if (GetWindowIcon(child) is { } icon)
                {
                    result = icon;
                    return false;
                }

                if (GetWindowIconFromChildWindows(child) is { } nestedIcon)
                {
                    result = nestedIcon;
                    return false;
                }

                return true;
            }, IntPtr.Zero);
            return result;
        }

        private System.Windows.Media.ImageSource? GetShellAppIcon(IntPtr hWnd)
        {
            string? appId = GetWindowAppUserModelID(hWnd);
            if (string.IsNullOrEmpty(appId))
            {
                return null;
            }

            string parsingName = $"shell:AppsFolder\\{appId}";
            var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            if (NativeMethods.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out IntPtr imageFactoryPtr) != 0)
            {
                return null;
            }

            IShellItemImageFactory? imageFactory = null;
            try
            {
                imageFactory = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(imageFactoryPtr);
                var size = new NativeMethods.SIZE { cx = 32, cy = 32 };
                if (imageFactory.GetImage(size, NativeMethods.SIIGBF.RESIZETOFIT, out IntPtr hBitmap) == 0 && hBitmap != IntPtr.Zero)
                {
                    var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        IntPtr.Zero,
                        System.Windows.Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    bitmapSource.Freeze();
                    NativeMethods.DeleteObject(hBitmap);
                    return bitmapSource;
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (imageFactory != null)
                {
                    Marshal.ReleaseComObject(imageFactory);
                }
            }

            return null;
        }

        private string? GetWindowAppUserModelID(IntPtr hWnd)
        {
            var appId = GetWindowAppModelIDFromWindow(hWnd);
            if (!string.IsNullOrEmpty(appId))
            {
                return appId;
            }

            string? childAppId = null;
            NativeMethods.EnumChildWindows(hWnd, (child, lparam) =>
            {
                childAppId = GetWindowAppModelIDFromWindow(child);
                return string.IsNullOrEmpty(childAppId);
            }, IntPtr.Zero);
            return childAppId;
        }

        private string? GetWindowAppModelIDFromWindow(IntPtr hWnd)
        {
            var iid = new Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
            if (NativeMethods.SHGetPropertyStoreForWindow(hWnd, ref iid, out NativeMethods.IPropertyStore propertyStore) != 0)
            {
                return null;
            }

            NativeMethods.PropVariant propVar = new();
            try
            {
                var propertyKey = NativeMethods.PKEY_AppUserModel_ID;
                if (propertyStore.GetValue(ref propertyKey, out propVar) == 0 && propVar.vt == (ushort)VarEnum.VT_LPWSTR)
                {
                    string? value = Marshal.PtrToStringUni(propVar.p);
                    return value;
                }
            }
            catch
            {
            }
            finally
            {
                NativeMethods.PropVariantClear(ref propVar);
                Marshal.ReleaseComObject(propertyStore);
            }

            return null;
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        private interface IShellItemImageFactory
        {
            int GetImage(NativeMethods.SIZE size, NativeMethods.SIIGBF flags, out IntPtr phbm);
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
