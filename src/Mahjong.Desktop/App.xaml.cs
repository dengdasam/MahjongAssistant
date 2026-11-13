using System.Windows;
using Mahjong.Vision;

namespace Mahjong.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        NativeWindow.EnablePerMonitorDpi();
        base.OnStartup(e);
    }
}
