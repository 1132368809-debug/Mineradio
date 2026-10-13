using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal enum DesktopMode
{
    DesktopLayer,
    OverlayFallback,
    Disabled
}

internal enum DesktopAttachStatus
{
    NotStarted,
    AttachedToDesktopLayer,
    OverlayFallbackActive,
    DisabledWithReason,
    Failed
}

internal sealed class DesktopAttachResult
{
    private DesktopAttachResult(bool ok, bool changed, IntPtr parent, Rectangle bounds, string key, string reason, DesktopAttachStatus status)
    {
        Ok = ok;
        Changed = changed;
        Parent = parent;
        Bounds = bounds;
        Key = key;
        Reason = reason;
        Status = status;
    }

    public bool Ok { get; }
    public bool Changed { get; }
    public IntPtr Parent { get; }
    public Rectangle Bounds { get; }
    public string Key { get; }
    public string Reason { get; }
    public DesktopAttachStatus Status { get; }

    public static DesktopAttachResult Attached(bool changed, IntPtr parent, Rectangle bounds, string key)
        => new(true, changed, parent, bounds, key, "", DesktopAttachStatus.AttachedToDesktopLayer);

    public static DesktopAttachResult Disabled(string reason)
        => new(false, true, IntPtr.Zero, Rectangle.Empty, "", reason, DesktopAttachStatus.DisabledWithReason);
}

internal sealed class DesktopAttachManager
{
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_CHILD = 0x40000000L;
    private const long WS_VISIBLE = 0x10000000L;
    private const long WS_POPUP = 0x80000000L;
    private const long WS_CAPTION = 0x00C00000L;
    private const long WS_THICKFRAME = 0x00040000L;
    private const long WS_EX_TOPMOST = 0x00000008L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_TRANSPARENT = 0x00000020L;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000L;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint GW_OWNER = 4;
    private static readonly IntPtr HWND_TOP = new(0);
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private readonly Action<string> _log;
    private long _lastDumpAt;

    public DesktopAttachManager(Action<string> log)
    {
        _log = log;
    }

    public static string ModeName(DesktopMode mode)
    {
        return mode switch
        {
            DesktopMode.DesktopLayer => "desktop-layer",
            DesktopMode.OverlayFallback => "overlay-fallback",
            DesktopMode.Disabled => "disabled",
            _ => "unknown"
        };
    }

    public DesktopAttachResult AttachToDesktopLayer(IntPtr mineradioHwnd, bool force, IntPtr knownParent, Rectangle knownBounds, string knownKey)
    {
        if (mineradioHwnd == IntPtr.Zero || !IsWindow(mineradioHwnd))
        {
            return Disable("mineradio hwnd invalid");
        }

        var target = ResolveTarget(force);
        if (target.Parent == IntPtr.Zero)
        {
            return Disable(target.Reason);
        }

        var parent = GetParent(mineradioHwnd);
        var currentRect = WindowRect(mineradioHwnd);
        var changed = force
            || parent != target.Parent
            || knownParent != target.Parent
            || knownKey != target.Key
            || !SameRect(knownBounds, target.Bounds)
            || !SameRect(currentRect, target.Bounds)
            || !IsWindowVisible(mineradioHwnd);

        if (!changed)
        {
            return DesktopAttachResult.Attached(false, target.Parent, target.Bounds, target.Key);
        }

        _log($"[DesktopAttach] Attach requested: target={target.Key} parent=0x{target.Parent.ToInt64():X} bounds={RectText(target.Bounds)} currentParent=0x{parent.ToInt64():X}");
        ApplyChildWindowStyles(mineradioHwnd);

        var previousParent = SetParent(mineradioHwnd, target.Parent);
        var parentAfter = GetParent(mineradioHwnd);
        _log($"[DesktopAttach] SetParent result previous=0x{previousParent.ToInt64():X} parentAfter=0x{parentAfter.ToInt64():X} target=0x{target.Parent.ToInt64():X}");
        if (parentAfter != target.Parent)
        {
            return Disable($"SetParent failed parentAfter=0x{parentAfter.ToInt64():X}");
        }

        var insertAfter = target.Key.StartsWith("desktop-host:", StringComparison.OrdinalIgnoreCase)
            ? HWND_BOTTOM
            : HWND_TOP;
        var insertLabel = insertAfter == HWND_BOTTOM
            ? "HWND_BOTTOM(below desktop icons)"
            : "HWND_TOP(non-topmost desktop sibling)";
        var ok = SetWindowPos(
            mineradioHwnd,
            insertAfter,
            target.Bounds.X,
            target.Bounds.Y,
            Math.Max(1, target.Bounds.Width),
            Math.Max(1, target.Bounds.Height),
            SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        _log($"[DesktopAttach] SetWindowPos {insertLabel} ok={ok} bounds={RectText(target.Bounds)}");
        if (!ok)
        {
            return Disable($"SetWindowPos failed win32={Marshal.GetLastWin32Error()}");
        }

        return DesktopAttachResult.Attached(true, target.Parent, target.Bounds, target.Key);
    }

    private DesktopAttachResult Disable(string reason)
    {
        reason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
        _log($"[DesktopAttach] FAILED: {reason}");
        _log("[DesktopAttach] Mode changed to disabled-with-reason");
        _log("[DesktopAttach] Not falling back to TOPMOST automatically");
        return DesktopAttachResult.Disabled(reason);
    }

    private DesktopTarget ResolveTarget(bool forceDump)
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) progman = FindWindow("Progman", "Program Manager");
        _log($"[DesktopAttach] Progman hwnd=0x{progman.ToInt64():X}");
        if (progman == IntPtr.Zero) return DesktopTarget.Failed("Progman not found");

        TriggerWorkerW(progman);

        var windows = EnumerateTopLevelWindows();
        var now = Environment.TickCount64;
        if (forceDump || now - _lastDumpAt > 300000)
        {
            DumpDesktopHwndTree(windows);
            _lastDumpAt = now;
        }

        var iconHost = windows.FirstOrDefault(w => HasDescendantClass(w.Hwnd, "SHELLDLL_DefView"));
        if (iconHost.Hwnd != IntPtr.Zero)
        {
            _log($"[DesktopAttach] Found SHELLDLL_DefView under hwnd=0x{iconHost.Hwnd.ToInt64():X} class={iconHost.ClassName}");
            var shell = FindDescendant(iconHost.Hwnd, "SHELLDLL_DefView");
            var list = shell != IntPtr.Zero ? FindDescendant(shell, "SysListView32") : IntPtr.Zero;
            if (list != IntPtr.Zero) _log($"[DesktopAttach] Found SysListView32 under hwnd=0x{shell.ToInt64():X}");
        }

        var wpeCandidates = windows
            .Where(w => IsMeaningfulWallpaperEngineWindow(w) || HasMeaningfulWallpaperEngineDescendant(w.Hwnd))
            .OrderByDescending(w => Area(w.Rect))
            .ToList();
        foreach (var wpe in wpeCandidates.Take(8))
        {
            _log($"[DesktopAttach] WPE candidate hwnd=0x{wpe.Hwnd.ToInt64():X} class={wpe.ClassName} process={wpe.ProcessName} pid={wpe.Pid} rect={RectText(wpe.Rect)}");
        }

        var wpeWorker = windows.FirstOrDefault(w => IsWorkerW(w) && LooksLikeDesktopSized(w.Rect) && HasMeaningfulWallpaperEngineDescendant(w.Hwnd));
        var targetParent = wpeWorker.Hwnd;
        var keyPrefix = "wpe-workerw";
        if (targetParent == IntPtr.Zero)
        {
            var workerWithoutIcons = windows.FirstOrDefault(w => IsWorkerW(w) && LooksLikeDesktopSized(w.Rect) && !HasDescendantClass(w.Hwnd, "SHELLDLL_DefView"));
            targetParent = workerWithoutIcons.Hwnd;
            keyPrefix = "workerw";
        }
        if (targetParent == IntPtr.Zero)
        {
            var childWorker = EnumerateChildWindows(progman)
                .FirstOrDefault(w => IsWorkerW(w) && LooksLikeDesktopSized(w.Rect) && !HasDescendantClass(w.Hwnd, "SHELLDLL_DefView"));
            if (childWorker.Hwnd != IntPtr.Zero)
            {
                targetParent = childWorker.Hwnd;
                keyPrefix = "progman-child-workerw";
                _log($"[DesktopAttach] using Progman child WorkerW hwnd=0x{targetParent.ToInt64():X} rect={RectText(childWorker.Rect)}");
            }
        }
        if (targetParent == IntPtr.Zero)
        {
            _log("[DesktopAttach] full-size WorkerW not resolved, using desktop icon host layer");
            targetParent = iconHost.Hwnd != IntPtr.Zero ? iconHost.Hwnd : progman;
            keyPrefix = "desktop-host";
        }
        if (targetParent == IntPtr.Zero) return DesktopTarget.Failed("target WorkerW/Progman not found");

        var targetBounds = ResolveTargetMonitorBounds(wpeCandidates.FirstOrDefault());
        var monitor = Screen.FromRectangle(targetBounds);
        _log($"[DesktopAttach] Target monitor: x={targetBounds.X} y={targetBounds.Y} w={targetBounds.Width} h={targetBounds.Height} primary={monitor.Primary}");
        _log("[DesktopAttach] Mineradio resized to monitor rect");
        return new DesktopTarget(targetParent, targetBounds, $"{keyPrefix}:0x{targetParent.ToInt64():X}/monitor:{targetBounds.X},{targetBounds.Y},{targetBounds.Width},{targetBounds.Height}", "");
    }

    private void TriggerWorkerW(IntPtr progman)
    {
        var ok = SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out var result);
        _log($"[DesktopAttach] SendMessageTimeout Progman 0x052C ok={ok} result=0x{result.ToInt64():X} win32={Marshal.GetLastWin32Error()}");
    }

    private Rectangle ResolveTargetMonitorBounds(WindowInfo wpe)
    {
        if (wpe.Hwnd == IntPtr.Zero || wpe.Rect.Width <= 0 || wpe.Rect.Height <= 0)
        {
            _log("[DesktopAttach] monitor-specific WorkerW not resolved, using primary desktop WorkerW");
            return Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
        }
        return Screen.FromRectangle(wpe.Rect).Bounds;
    }

    private void ApplyChildWindowStyles(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        var nextStyle = (style | WS_CHILD | WS_VISIBLE) & ~WS_POPUP & ~WS_CAPTION & ~WS_THICKFRAME;
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(nextStyle));

        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        var nextExStyle = (exStyle | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP) & ~WS_EX_TOPMOST;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(nextExStyle));
        _log($"[DesktopAttach] style old=0x{style:X} new=0x{nextStyle:X} exOld=0x{exStyle:X} exNew=0x{nextExStyle:X}");
    }

    private void DumpDesktopHwndTree(List<WindowInfo> windows)
    {
        _log("[DesktopAttach] DumpDesktopHwndTree begin");
        var index = 0;
        foreach (var window in windows)
        {
            DumpWindow(window, index, 0);
            if (IsRelevantForChildDump(window))
            {
                var childIndex = 0;
                foreach (var child in EnumerateChildWindows(window.Hwnd).Take(80))
                {
                    DumpWindow(child, childIndex++, 1);
                }
            }
            index++;
        }
        _log("[DesktopAttach] DumpDesktopHwndTree end");
    }

    private void DumpWindow(WindowInfo window, int z, int depth)
    {
        var mark = window.IsWallpaperEngineCandidate ? " [WPE?]" : "";
        _log($"[DesktopAttach] {(depth > 0 ? "child" : "top")} z={z} hwnd=0x{window.Hwnd.ToInt64():X} parent=0x{window.Parent.ToInt64():X} owner=0x{window.Owner.ToInt64():X} class={window.ClassName} title=\"{TrimForLog(window.Title)}\" process={window.ProcessName} pid={window.Pid} visible={window.Visible} rect={RectText(window.Rect)}{mark}");
    }

    private bool IsRelevantForChildDump(WindowInfo window)
    {
        return IsWorkerW(window)
            || string.Equals(window.ClassName, "Progman", StringComparison.OrdinalIgnoreCase)
            || IsMeaningfulWallpaperEngineWindow(window)
            || HasMeaningfulWallpaperEngineDescendant(window.Hwnd)
            || HasDescendantClass(window.Hwnd, "SHELLDLL_DefView");
    }

    private List<WindowInfo> EnumerateTopLevelWindows()
    {
        var windows = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            windows.Add(ReadWindowInfo(hwnd));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private List<WindowInfo> EnumerateChildWindows(IntPtr root)
    {
        var windows = new List<WindowInfo>();
        EnumChildWindows(root, (hwnd, _) =>
        {
            windows.Add(ReadWindowInfo(hwnd));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private WindowInfo ReadWindowInfo(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return new WindowInfo(
            hwnd,
            GetParent(hwnd),
            GetWindow(hwnd, GW_OWNER),
            ClassName(hwnd),
            WindowText(hwnd),
            ProcessName(pid),
            pid,
            IsWindowVisible(hwnd),
            WindowRect(hwnd));
    }

    private static bool IsWorkerW(WindowInfo window)
    {
        return string.Equals(window.ClassName, "WorkerW", StringComparison.OrdinalIgnoreCase);
    }

    private bool HasMeaningfulWallpaperEngineDescendant(IntPtr root)
    {
        var found = false;
        EnumChildWindows(root, (hwnd, _) =>
        {
            var info = ReadWindowInfo(hwnd);
            if (IsMeaningfulWallpaperEngineWindow(info))
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool IsMeaningfulWallpaperEngineWindow(WindowInfo window)
    {
        if (!window.IsWallpaperEngineCandidate) return false;
        if (window.Pid == (uint)Environment.ProcessId) return false;
        if (Contains(window.ClassName, "WPETrayWindow") || Contains(window.ClassName, "WPEEventWindow")) return false;
        if (window.Rect.Width < 320 || window.Rect.Height < 180) return false;
        return true;
    }

    private static bool LooksLikeDesktopSized(Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return false;
        foreach (var screen in Screen.AllScreens)
        {
            var bounds = screen.Bounds;
            if (rect.Width >= bounds.Width * 0.75 && rect.Height >= bounds.Height * 0.75) return true;
        }
        return false;
    }

    private static bool HasDescendantClass(IntPtr root, string className)
    {
        return FindDescendant(root, className) != IntPtr.Zero;
    }

    private static IntPtr FindDescendant(IntPtr root, string className)
    {
        var direct = FindWindowEx(root, IntPtr.Zero, className, null);
        if (direct != IntPtr.Zero) return direct;

        var result = IntPtr.Zero;
        EnumChildWindows(root, (hwnd, _) =>
        {
            if (result != IntPtr.Zero) return false;
            if (string.Equals(ClassName(hwnd), className, StringComparison.OrdinalIgnoreCase))
            {
                result = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static Rectangle WindowRect(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect)) return Rectangle.Empty;
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static int Area(Rectangle rect)
    {
        return Math.Max(0, rect.Width) * Math.Max(0, rect.Height);
    }

    private static bool Contains(string value, string needle)
    {
        return value?.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool SameRect(Rectangle a, Rectangle b)
    {
        return a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;
    }

    private static string RectText(Rectangle rect)
    {
        return $"{rect.X},{rect.Y},{rect.Width},{rect.Height}";
    }

    private static string TrimForLog(string text)
    {
        text = text ?? "";
        return text.Length <= 96 ? text : text[..96];
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static string WindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static string ProcessName(uint pid)
    {
        if (pid == 0) return "";
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    private readonly struct DesktopTarget
    {
        public DesktopTarget(IntPtr parent, Rectangle bounds, string key, string reason)
        {
            Parent = parent;
            Bounds = bounds;
            Key = key;
            Reason = reason;
        }

        public IntPtr Parent { get; }
        public Rectangle Bounds { get; }
        public string Key { get; }
        public string Reason { get; }

        public static DesktopTarget Failed(string reason) => new(IntPtr.Zero, Rectangle.Empty, "", reason);
    }

    private readonly struct WindowInfo
    {
        public WindowInfo(IntPtr hwnd, IntPtr parent, IntPtr owner, string className, string title, string processName, uint pid, bool visible, Rectangle rect)
        {
            Hwnd = hwnd;
            Parent = parent;
            Owner = owner;
            ClassName = className ?? "";
            Title = title ?? "";
            ProcessName = processName ?? "";
            Pid = pid;
            Visible = visible;
            Rect = rect;
        }

        public IntPtr Hwnd { get; }
        public IntPtr Parent { get; }
        public IntPtr Owner { get; }
        public string ClassName { get; }
        public string Title { get; }
        public string ProcessName { get; }
        public uint Pid { get; }
        public bool Visible { get; }
        public Rectangle Rect { get; }

        public bool IsWallpaperEngineCandidate
        {
            get
            {
                if (Pid == (uint)Environment.ProcessId) return false;
                var explicitWallpaperSignal = Contains(ProcessName, "wallpaper")
                    || Contains(ProcessName, "webwallpaper")
                    || Contains(Title, "Wallpaper Engine")
                    || Contains(Title, "Wallpaper")
                    || Contains(ClassName, "WPE")
                    || Contains(ClassName, "Wallpaper");
                if (explicitWallpaperSignal) return true;

                var possibleRenderer = Contains(ClassName, "CEF")
                    || Contains(ClassName, "Chrome_WidgetWin")
                    || Contains(ClassName, "Qt")
                    || Contains(ClassName, "SDL")
                    || Contains(ClassName, "Unity")
                    || Contains(ClassName, "Unreal");
                return possibleRenderer && (
                    Contains(ProcessName, "wallpaper")
                    || Contains(Title, "Wallpaper")
                    || Contains(Title, "WPE"));
            }
        }

        private static bool Contains(string value, string needle)
        {
            return value?.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
