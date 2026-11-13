using System.Runtime.InteropServices;

namespace Mahjong.Desktop;

/// <summary>
/// One global hotkey: Ctrl+Shift+H hides/shows the floating card, so it never
/// accidentally shows up in a screen share or a recording.
/// </summary>
internal static class Hotkey
{
    public const int WmHotkey = 0x0312;
    public const int ToggleOverlayId = 0x4D4A;

    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint KeyH = 0x48;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public static bool Register(IntPtr hwnd) =>
        RegisterHotKey(hwnd, ToggleOverlayId, ModControl | ModShift | ModNoRepeat, KeyH);

    public static void Unregister(IntPtr hwnd) => UnregisterHotKey(hwnd, ToggleOverlayId);
}
