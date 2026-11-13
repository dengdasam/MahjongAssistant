using System.IO;
using System.Text;

namespace Mahjong.Desktop;

/// <summary>
/// Tiny append-only log so a run can be reviewed afterwards — useful when the
/// overlay shows nothing and the reason is off-screen. Rolls over at 1 MB.
/// </summary>
public static class DiagnosticsLog
{
    private const long MaxBytes = 1024 * 1024;

    private static readonly object Gate = new();
    private static string? _path;

    public static string? Path => _path;

    public static void Init(string path)
    {
        try
        {
            var full = System.IO.Path.IsPathRooted(path)
                ? path
                : System.IO.Path.Combine(AppContext.BaseDirectory, path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            _path = full;
            Write("=== session start ===");
        }
        catch
        {
            _path = null;
        }
    }

    public static void Write(string message)
    {
        if (_path is null) return;
        try
        {
            lock (Gate)
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes) File.WriteAllText(_path, string.Empty, Encoding.UTF8);
                File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // logging must never break the app
        }
    }
}
