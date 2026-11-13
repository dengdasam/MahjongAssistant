using System.Drawing;
using System.Drawing.Imaging;

namespace Mahjong.Vision;

/// <summary>
/// GDI screen capture of a window's client area — the .NET counterpart of
/// PIL ImageGrab used by the original. Reads what is actually on screen, so an
/// occluded window cannot be captured (same limitation as the original).
/// </summary>
public static class ScreenCapture
{
    public static Bitmap CaptureClientArea(IntPtr hwnd)
    {
        var area = NativeWindow.GetClientAreaOnScreen(hwnd);
        if (area.Width <= 0 || area.Height <= 0)
            throw new InvalidOperationException("window client area is not available");

        var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
        bitmap.SetResolution(96f, 96f);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(area.Left, area.Top, 0, 0, new Size(area.Width, area.Height), CopyPixelOperation.SourceCopy);
        return bitmap;
    }
}
