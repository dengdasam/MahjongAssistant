namespace Mahjong.Vision;

/// <summary>
/// Where the suggestion card should sit relative to the table window.
///
/// Split out of the WPF window because the two coordinate systems are the classic
/// source of "the card is on the wrong monitor / half off-screen" bugs: Win32
/// reports physical pixels, WPF positions windows in device-independent units, and
/// the ratio between them depends on the DPI of the monitor the *table* is on.
/// </summary>
public static class OverlayGeometry
{
    /// <summary>Gap between the table window and the card, in physical pixels.</summary>
    public const double Gap = 12;

    /// <summary>Vertical offset from the table's top edge, in physical pixels.</summary>
    public const double TopOffset = 90;

    /// <summary>Minimum margin from the work-area edges, in physical pixels.</summary>
    public const double EdgeMargin = 8;

    /// <summary>
    /// Position (in DIPs) for a card of <paramref name="cardWidth"/> beside
    /// <paramref name="target"/>, flipping to the left side when there is no room
    /// on the right, and never leaving the monitor's work area.
    /// </summary>
    public static (double Left, double Top) Beside(
        NativeWindow.Rect target,
        NativeWindow.Rect workArea,
        int dpi,
        double cardWidth)
    {
        var scale = dpi <= 0 ? 1.0 : 96.0 / dpi;

        var left = (target.Right + Gap) * scale;
        var rightLimit = workArea.Width > 0 ? workArea.Right * scale : double.MaxValue;
        if (left + cardWidth > rightLimit)
        {
            left = Math.Max(
                workArea.Left * scale + EdgeMargin,
                target.Left * scale - cardWidth - Gap * scale);
        }

        var top = Math.Max(workArea.Top * scale + EdgeMargin, target.Top * scale + TopOffset * scale);
        return (left, top);
    }
}
