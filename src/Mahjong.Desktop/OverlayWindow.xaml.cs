using System.Windows;
using System.Windows.Input;
using Mahjong.Core;
using Mahjong.Vision;

namespace Mahjong.Desktop;

public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Place the card beside the game window, clamped to that monitor's work area.
    /// All inputs are physical pixels (as Win32 reports them); the conversion to
    /// WPF's device-independent units happens inside <see cref="OverlayGeometry"/>.
    /// </summary>
    public void PlaceNear(NativeWindow.Rect target, NativeWindow.Rect workArea, int dpi = 96)
    {
        if (target.Width <= 0 || target.Height <= 0) return;

        var (left, top) = OverlayGeometry.Beside(target, workArea, dpi, ActualWidth > 0 ? ActualWidth : 220);
        Left = left;
        Top = top;
    }

    public void UpdateCard(DiscardOption? option)
    {
        if (option is null)
        {
            TileText.Text = "—";
            DetailText.Text = "等待识别";
            AltText.Text = string.Empty;
            StageText.Text = string.Empty;
            return;
        }

        TileText.Text = option.Tile;
        DetailText.Text = $"向听 {option.Shanten} · 进张 {option.Ukeire} 张";
        AltText.Text = option.OpponentRisk.Count > 0
            ? $"点炮风险 {option.RiskSummary}（启发式档位）"
            : string.Empty;
        StageText.Text = option.Waits.Count > 0 ? "听牌" : string.Empty;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
