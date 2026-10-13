using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

internal readonly record struct FullscreenAppSnapshot(
    uint ProcessId,
    string WindowClass,
    Rectangle WindowBounds,
    Rectangle MonitorBounds);

internal static class FullscreenAppDetector
{
    private const int GwlStyle = -16;
    private const uint GaRoot = 2;
    private const uint MonitorDefaultToNearest = 2;
    private const uint DwmwaExtendedFrameBounds = 9;
    private const uint DwmwaCloaked = 14;
    private const long WsPopup = 0x80000000L;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const int BoundsTolerance = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr window,
        uint attribute,
        out Rect value,
        int valueSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr window,
        uint attribute,
        out int value,
        int valueSize);

    public static bool TryGetForegroundFullscreen(
        IntPtr ignoredWindow,
        out FullscreenAppSnapshot snapshot)
    {
        snapshot = default;
        if (!OperatingSystem.IsWindows()) return false;

        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false;
        var root = GetAncestor(window, GaRoot);
        if (root != IntPtr.Zero) window = root;
        if (window == ignoredWindow || !IsWindowVisible(window) || IsIconic(window)) return false;

        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == Environment.ProcessId) return false;

        var windowClass = ReadWindowClass(window);
        if (IsDesktopShellSurface(windowClass)) return false;

        if (DwmGetWindowAttribute(
                window,
                DwmwaCloaked,
                out int cloaked,
                Marshal.SizeOf<int>()) == 0
            && cloaked != 0)
        {
            return false;
        }

        if (!TryGetWindowBounds(window, out var windowBounds)) return false;
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return false;
        var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return false;

        var monitorBounds = ToRectangle(monitorInfo.Monitor);
        var style = GetWindowLongPtr(window, GwlStyle).ToInt64();
        if (!IsFullscreenCandidate(windowBounds, monitorBounds, style)) return false;

        snapshot = new FullscreenAppSnapshot(
            processId,
            windowClass,
            windowBounds,
            monitorBounds);
        return true;
    }

    internal static bool IsFullscreenCandidate(
        Rectangle windowBounds,
        Rectangle monitorBounds,
        long windowStyle)
    {
        if (!CoversMonitor(windowBounds, monitorBounds, BoundsTolerance)) return false;

        var hasPopupStyle = (windowStyle & WsPopup) != 0;
        var hasCaption = (windowStyle & WsCaption) != 0;
        var hasResizableFrame = (windowStyle & WsThickFrame) != 0;
        return hasPopupStyle || (!hasCaption && !hasResizableFrame);
    }

    private static bool CoversMonitor(
        Rectangle windowBounds,
        Rectangle monitorBounds,
        int tolerance)
    {
        if (windowBounds.Width <= 0
            || windowBounds.Height <= 0
            || monitorBounds.Width <= 0
            || monitorBounds.Height <= 0)
        {
            return false;
        }

        return windowBounds.Left <= monitorBounds.Left + tolerance
            && windowBounds.Top <= monitorBounds.Top + tolerance
            && windowBounds.Right >= monitorBounds.Right - tolerance
            && windowBounds.Bottom >= monitorBounds.Bottom - tolerance;
    }

    private static bool TryGetWindowBounds(IntPtr window, out Rectangle bounds)
    {
        if (DwmGetWindowAttribute(
                window,
                DwmwaExtendedFrameBounds,
                out Rect rect,
                Marshal.SizeOf<Rect>()) != 0
            && !GetWindowRect(window, out rect))
        {
            bounds = Rectangle.Empty;
            return false;
        }

        bounds = ToRectangle(rect);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private static Rectangle ToRectangle(Rect rect)
    {
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static string ReadWindowClass(IntPtr window)
    {
        var className = new StringBuilder(256);
        return GetClassName(window, className, className.Capacity) > 0
            ? className.ToString()
            : "";
    }

    private static bool IsDesktopShellSurface(string windowClass)
    {
        return string.Equals(windowClass, "Progman", StringComparison.OrdinalIgnoreCase)
            || string.Equals(windowClass, "WorkerW", StringComparison.OrdinalIgnoreCase)
            || string.Equals(windowClass, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(windowClass, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase);
    }
}
