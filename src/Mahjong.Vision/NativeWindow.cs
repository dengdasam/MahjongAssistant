using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Mahjong.Vision;

public sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName, int ClientWidth, int ClientHeight);

/// <summary>
/// Win32 window enumeration + client-area geometry. Replaces the ctypes/wintypes
/// part of the original <c>list_game_windows</c> / <c>client_rect</c>.
/// </summary>
public static class NativeWindow
{
    public const int SwRestore = 9;

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int DwmwaExtendedFrameBounds = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref Point point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);

    [DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(int value);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    public static void EnablePerMonitorDpi()
    {
        try { SetProcessDpiAwareness(2); } catch { /* already set by the host */ }
    }

    public static IReadOnlyList<WindowInfo> ListVisibleWindows()
    {
        var result = new List<WindowInfo>();
        var self = Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindowTextLength(hwnd) == 0) return true;
            if (GetWindow(hwnd, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            if ((GetWindowLong(hwnd, GwlExStyle) & WsExToolWindow) != 0) return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == self) return true;

            var title = new StringBuilder(GetWindowTextLength(hwnd) + 2);
            GetWindowText(hwnd, title, title.Capacity);
            if (title.Length == 0) return true;

            if (!GetClientRect(hwnd, out var client) || client.Width < 200 || client.Height < 200) return true;

            var processName = TryGetProcessName((int)pid);
            result.Add(new WindowInfo(hwnd, title.ToString(), processName, client.Width, client.Height));
            return true;
        }, IntPtr.Zero);

        return result.OrderBy(w => w.ProcessName).ThenBy(w => w.Title).ToList();
    }

    private static string TryGetProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch { return "?"; }
    }

    /// <summary>Client area in physical screen pixels.</summary>
    public static Rect GetClientAreaOnScreen(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out var client)) return default;
        var origin = new Point { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref origin)) return default;
        return new Rect { Left = origin.X, Top = origin.Y, Right = origin.X + client.Width, Bottom = origin.Y + client.Height };
    }

    public static Rect GetWindowBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var bounds, Marshal.SizeOf<Rect>()) == 0)
            return bounds;
        return GetWindowRect(hwnd, out var rect) ? rect : default;
    }

    public static void Focus(IntPtr hwnd)
    {
        ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
    }

    /// <summary>
    /// DPI of the monitor the window sits on (96 = 100%). Everything returned by this
    /// class is in physical pixels, while WPF positions windows in DIPs, so callers
    /// must divide by <c>dpi / 96</c> before assigning <c>Left</c>/<c>Top</c>.
    /// </summary>
    public static int DpiFor(IntPtr hwnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? 96 : (int)dpi;
        }
        catch (EntryPointNotFoundException)
        {
            return 96; // pre-Windows 10 1607
        }
        catch (DllNotFoundException)
        {
            return 96;
        }
    }

    /// <summary>Work area (screen minus taskbar) of the monitor hosting the window, in physical pixels.</summary>
    public static Rect GetWorkArea(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return default;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(monitor, ref info) ? info.Work : default;
    }

    /// <summary>True when the handle still refers to a live window.</summary>
    public static bool IsAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd) && IsWindowVisible(hwnd);

    public static bool IsMinimized(IntPtr hwnd) => hwnd != IntPtr.Zero && IsIconic(hwnd);

    public static Rect GetClientSize(IntPtr hwnd) =>
        GetClientRect(hwnd, out var rect) ? rect : default;

    /// <summary>
    /// Picks the most likely game window: keyword match on title or process name,
    /// largest client area first. Mirrors the original keyword filter
    /// ('微乐', '麻将', '微信', 'wechat').
    /// </summary>
    public static WindowInfo? FindGameWindow(IEnumerable<string> keywords)
    {
        var terms = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim().ToLowerInvariant())
            .ToArray();
        if (terms.Length == 0) return null;

        return ListVisibleWindows()
            .Where(w =>
            {
                var title = w.Title.ToLowerInvariant();
                var process = w.ProcessName.ToLowerInvariant();
                return terms.Any(t => title.Contains(t) || process.Contains(t));
            })
            .OrderByDescending(w => (long)w.ClientWidth * w.ClientHeight)
            .FirstOrDefault();
    }
}
