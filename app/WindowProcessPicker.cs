using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace MemoryStudio;

public sealed record WindowTarget(nint Handle, int ProcessId, string ProcessName, string WindowTitle)
{
    public string DisplayName => $"{ProcessName} · PID {ProcessId}";
}

public static class WindowProcessPicker
{
    public static WindowTarget? ResolveCursor(bool allowCurrentProcess = false) => ResolveWindow(WindowAtCursor(), allowCurrentProcess);

    public static WindowTarget? ResolveWindow(nint hwnd, bool allowCurrentProcess = false)
    {
        if (hwnd == 0 || !Native.IsWindow(hwnd)) return null;
        nint root = Native.GetAncestor(hwnd, 2); // GA_ROOT converts controls to their top-level window.
        if (root == 0) root = hwnd;
        if (!Native.IsWindowVisible(root) || root == Native.GetDesktopWindow() || root == Native.GetShellWindow()) return null;
        var className = new StringBuilder(256);
        Native.GetClassName(root, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return null;
        Native.GetWindowThreadProcessId(root, out uint pid);
        if (pid == 0 || pid > int.MaxValue || (!allowCurrentProcess && pid == Environment.ProcessId)) return null;
        string processName;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (process.HasExited) return null;
            processName = process.ProcessName;
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
        var title = new StringBuilder(1024);
        Native.GetWindowText(root, title, title.Capacity);
        return new WindowTarget(root, (int)pid, processName, title.ToString());
    }

    internal static nint WindowAtCursor()
    {
        if (!Native.GetCursorPos(out Native.Point point)) return 0;
        return Native.WindowFromPoint(point);
    }

    internal sealed class Highlight : IDisposable
    {
        private readonly Window _window;
        private Native.Rect _lastRect;
        private bool _hasRect, _disposed;
        public nint Handle { get; private set; }

        public Highlight()
        {
            _window = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true, Background = Brushes.Transparent,
                ShowActivated = false, ShowInTaskbar = false, Topmost = true,
                IsHitTestVisible = false, Width = 1, Height = 1, Left = -32000, Top = -32000,
                Content = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0x57, 0xE0, 0xC1)), BorderThickness = new Thickness(3) }
            };
            _window.SourceInitialized += (_, _) =>
            {
                Handle = new WindowInteropHelper(_window).Handle;
                long style = Native.GetWindowLongPtr(Handle, -20).ToInt64();
                Native.SetWindowLongPtr(Handle, -20, (nint)(style | 0x20 | 0x80 | 0x08000000)); // Transparent, tool window, no activate.
                HwndSource.FromHwnd(Handle)?.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
                {
                    if (message == 0x84) { handled = true; return -1; } // WM_NCHITTEST / HTTRANSPARENT.
                    return 0;
                });
            };
        }

        public void ShowTarget(WindowTarget target)
        {
            if (_disposed || !Native.GetWindowRect(target.Handle, out var rect)) return;
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            if (width < 1 || height < 1) { Hide(); return; }
            if (!_window.IsVisible) _window.Show();
            if (_hasRect && rect.Equals(_lastRect)) return;
            _lastRect = rect; _hasRect = true;
            Native.SetWindowPos(Handle, -1, rect.Left, rect.Top, width, height, 0x10); // HWND_TOPMOST / SWP_NOACTIVATE; pixels avoid mixed-DPI drift.
            nint outer = Native.CreateRectRgn(0, 0, width, height);
            nint inner = Native.CreateRectRgn(4, 4, Math.Max(4, width - 4), Math.Max(4, height - 4));
            if (outer != 0 && inner != 0)
            {
                Native.CombineRgn(outer, outer, inner, 4); // A hollow native region keeps WindowFromPoint valid inside the frame.
                if (Native.SetWindowRgn(Handle, outer, true) == 0) Native.DeleteObject(outer);
            }
            else if (outer != 0) Native.DeleteObject(outer);
            if (inner != 0) Native.DeleteObject(inner);
        }

        public void Hide()
        {
            if (_disposed) return;
            _window.Hide();
            _hasRect = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _window.Close();
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
        [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint hwnd);
        [DllImport("user32.dll")] internal static extern nint GetDesktopWindow();
        [DllImport("user32.dll")] internal static extern nint GetShellWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint hwnd, StringBuilder text, int capacity);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd, StringBuilder text, int capacity);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("gdi32.dll")] internal static extern nint CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] internal static extern int CombineRgn(nint destination, nint source1, nint source2, int mode);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(nint value);
        [DllImport("user32.dll")] internal static extern int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    }
}
