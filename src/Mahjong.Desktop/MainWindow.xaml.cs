using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Mahjong.Core;
using Mahjong.Vision;
using DRect = System.Drawing.Rectangle;

namespace Mahjong.Desktop;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly DetectorOptions _options;
    private readonly HandStabilizer _stabilizer;
    private readonly FrameChangeDetector _change;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _scanTimer;

    private TileDetector? _detector;
    private OverlayWindow? _overlay;
    private HwndSource? _source;
    private IntPtr _target;
    private bool _busy;
    private bool _autoMode;
    private bool _pausedByUser;
    private bool _ready;
    private string? _lastStatus;

    /// <summary>Where the hand was last found, so the change test only watches that strip.</summary>
    private DRect? _handRegion;

    /// <summary>Last usable result, reused while the frame does not change.</summary>
    private FrameResult? _last;

    /// <summary>Table window bounds as of the last placement, for follow-the-window.</summary>
    private NativeWindow.Rect _lastBounds;

    /// <summary>Latches the "skipping because nothing moved" log line.</summary>
    private bool _skipLogged;

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        _options = _settings.ToDetectorOptions();
        _stabilizer = new HandStabilizer(_settings.HandStableFrames);
        _change = new FrameChangeDetector(maxSkip: _settings.StaticSkipMax);
        DiagnosticsLog.Init(_settings.LogPath);

        ModelText.Text = $"模型：{_settings.ModelPath}";

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_settings.IntervalMs) };
        _timer.Tick += OnTick;

        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_settings.AutoRescanMs) };
        _scanTimer.Tick += (_, _) => TryAutoAttach(silent: true);

        _autoMode = _settings.AutoStart;
        AutoModeCheck.IsChecked = _autoMode;

        RefreshWindows(updateStatus: true);

        _ready = true;
        Loaded += OnLoaded;
        Closed += (_, _) => Cleanup();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitDetector();
        if (_autoMode && _detector is not null) ScheduleFirstAttach();
    }

    // ---------- hotkey ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);

        if (Hotkey.Register(handle))
            DiagnosticsLog.Write("hotkey registered: Ctrl+Shift+H toggles the card");
        else
            DiagnosticsLog.Write("hotkey registration failed (already taken by another app)");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Hotkey.WmHotkey && wParam.ToInt32() == Hotkey.ToggleOverlayId)
        {
            ToggleOverlay();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void OnToggleOverlayClick(object sender, RoutedEventArgs e) => ToggleOverlay();

    private void ToggleOverlay()
    {
        if (_overlay is null || _target == IntPtr.Zero) return;

        if (_overlay.IsVisible)
        {
            _overlay.Hide();
            OverlayToggleButton.Content = "显示悬浮卡（Ctrl+Shift+H）";
        }
        else
        {
            _overlay.Show();
            OverlayToggleButton.Content = "隐藏悬浮卡（Ctrl+Shift+H）";
        }
    }

    /// <summary>Give the UI a beat to appear, then attach without user input.</summary>
    private void ScheduleFirstAttach()
    {
        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_settings.AutoStartDelayMs) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            TryAutoAttach(silent: false);
        };
        delay.Start();
    }

    private void InitDetector()
    {
        try
        {
            _detector?.Dispose();
            _detector = new TileDetector(_settings.ModelPath, _settings.ClassesPath);
            DiagnosticsLog.Write($"model loaded: {_settings.ModelPath}");
            StatusText.Text = _autoMode
                ? "模型已加载，正在查找牌局窗口…"
                : "模型已加载。选择牌桌窗口后点「开始」。";
        }
        catch (Exception ex)
        {
            _detector = null;
            DiagnosticsLog.Write($"model load failed: {ex.Message}");
            StatusText.Text = "模型加载失败：" + ex.Message;
        }
    }

    // ---------- window discovery ----------

    private void RefreshWindows(bool updateStatus)
    {
        var items = NativeWindow.ListVisibleWindows().Select(w => new WindowChoice(w)).ToList();
        var previous = (WindowList.SelectedItem as WindowChoice)?.Info.Handle ?? _target;

        WindowList.ItemsSource = items;
        var match = items.FirstOrDefault(c => c.Info.Handle == previous);
        WindowList.SelectedIndex = match is not null ? items.IndexOf(match) : (items.Count > 0 ? 0 : -1);

        if (updateStatus)
        {
            StatusText.Text = _autoMode
                ? $"找到 {items.Count} 个可见窗口，自动模式会在匹配到牌桌时开始。"
                : $"找到 {items.Count} 个可见窗口。";
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshWindows(updateStatus: true);

    private void OnReloadModelClick(object sender, RoutedEventArgs e)
    {
        InitDetector();
        if (_autoMode) TryAutoAttach(silent: false);
    }

    /// <summary>Find a table window by keyword, then start reading it.</summary>
    private void TryAutoAttach(bool silent)
    {
        if (!_ready || !_autoMode || _pausedByUser) return;
        if (_detector is null || _timer.IsEnabled) return;

        var found = NativeWindow.FindGameWindow(_settings.WindowKeywords);
        if (found is null)
        {
            DiagnosticsLog.Write("auto: no matching window");
            if (!silent) StatusText.Text = "自动模式：未找到牌局窗口，等待中（打开牌局后会自动开始）。";
            if (!_scanTimer.IsEnabled) _scanTimer.Start();
            return;
        }

        DiagnosticsLog.Write($"auto: picked {found.Title} / {found.ProcessName} {found.ClientWidth}x{found.ClientHeight}");

        _scanTimer.Stop();
        RefreshWindows(updateStatus: false);
        var match = (WindowList.ItemsSource as IEnumerable<WindowChoice>)?
            .FirstOrDefault(c => c.Info.Handle == found.Handle);
        if (match is not null) WindowList.SelectedItem = match;

        StartReading(found);
    }

    // ---------- reading ----------

    private void StartReading(WindowInfo info)
    {
        if (_detector is null)
        {
            StatusText.Text = "模型未加载，无法开始。";
            return;
        }

        _target = info.Handle;
        _pausedByUser = false;
        _stabilizer.Reset();
        _change.Reset();
        _handRegion = null;
        _last = null;

        _overlay ??= new OverlayWindow();
        PlaceOverlay();
        _overlay.UpdateCard(null);
        _overlay.Show();
        OverlayToggleButton.Content = "隐藏悬浮卡（Ctrl+Shift+H）";

        _timer.Start();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        DiagnosticsLog.Write($"start reading: {info.Title} hwnd=0x{info.Handle.ToInt64():X}");
        StatusText.Text = $"正在读取「{info.Title}」…请保持窗口可见、不要最小化。";
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (WindowList.SelectedItem is not WindowChoice choice)
        {
            StatusText.Text = "请先选择一个牌桌窗口。";
            return;
        }

        _scanTimer.Stop();
        StartReading(choice.Info);
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _pausedByUser = true;
        _scanTimer.Stop();
        StopReading("已停止。");
    }

    private void StopReading(string message)
    {
        _timer.Stop();
        _target = IntPtr.Zero;
        _change.Reset();
        _handRegion = null;
        _last = null;
        _overlay?.Hide();
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        StatusText.Text = message;
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_busy || _detector is null || _target == IntPtr.Zero) return;

        if (!NativeWindow.IsAlive(_target) || NativeWindow.IsMinimized(_target))
        {
            OnWindowLost(NativeWindow.IsMinimized(_target) ? "牌局窗口已最小化" : "牌局窗口已关闭");
            return;
        }

        _busy = true;
        try
        {
            var hwnd = _target;
            var detector = _detector;

            if (_settings.FollowWindow) FollowWindow(hwnd);

            var frame = await Task.Run(() => Analyze(hwnd, detector));
            _last = frame;
            Apply(frame);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write($"frame error: {ex.GetType().Name}: {ex.Message}");
            StatusText.Text = "读取失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Move the card with the table window; a moved table must not strand it.</summary>
    private void FollowWindow(IntPtr hwnd)
    {
        var bounds = NativeWindow.GetWindowBounds(hwnd);
        if (bounds.Width <= 0) return;

        if (bounds.Left == _lastBounds.Left && bounds.Top == _lastBounds.Top &&
            bounds.Width == _lastBounds.Width && bounds.Height == _lastBounds.Height)
        {
            return;
        }

        _lastBounds = bounds;
        PlaceOverlay();
        DiagnosticsLog.Write(
            $"card follows table: {bounds.Left},{bounds.Top} {bounds.Width}x{bounds.Height} " +
            $"dpi={NativeWindow.DpiFor(hwnd)} -> card at {_overlay?.Left:F0},{_overlay?.Top:F0}");
    }

    private void PlaceOverlay()
    {
        if (_overlay is null || _target == IntPtr.Zero) return;
        _overlay.PlaceNear(
            NativeWindow.GetWindowBounds(_target),
            NativeWindow.GetWorkArea(_target),
            NativeWindow.DpiFor(_target));
    }

    /// <summary>Keep the analysis paused but watch for the window coming back.</summary>
    private void OnWindowLost(string reason)
    {
        DiagnosticsLog.Write($"window lost: {reason}");
        _timer.Stop();
        _target = IntPtr.Zero;
        _change.Reset();
        _handRegion = null;
        _last = null;

        HandText.Text = "—";
        DiscardText.Text = "—";
        DetailText.Text = reason;
        AlternativesText.Text = string.Empty;
        _overlay?.UpdateCard(null);
        _overlay?.Hide();

        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;

        if (_autoMode && !_pausedByUser)
        {
            StatusText.Text = $"{reason}，恢复后会自动继续…";
            if (!_scanTimer.IsEnabled) _scanTimer.Start();
        }
        else
        {
            StatusText.Text = $"{reason}，已停止。";
        }
    }

    private void OnAutoModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;

        _autoMode = AutoModeCheck.IsChecked == true;
        if (_autoMode)
        {
            _pausedByUser = false;
            TryAutoAttach(silent: false);
        }
        else
        {
            _scanTimer.Stop();
        }
    }

    // ---------- analysis ----------

    private FrameResult Analyze(IntPtr hwnd, TileDetector detector)
    {
        using var bitmap = ScreenCapture.CaptureClientArea(hwnd);

        // Nothing moved since the last read: reuse it instead of paying for another
        // 250–800 ms inference. Deterministic input means the answer would be the
        // same, so this is safe for *any* previous result — but never while the
        // stabiliser is still waiting to confirm a change (it needs the next frame).
        var region = _handRegion ?? DefaultHandRegion(bitmap.Width, bitmap.Height);
        var changed = !_settings.StaticSkip || _change.HasChanged(bitmap, region);
        if (!changed && _last is not null && !_stabilizer.HasPending)
        {
            return _last with { Skipped = true, ElapsedMs = 0, Passes = 0 };
        }

        var frame = FrameAnalyzer.Analyze(bitmap, detector, _options);

        // Raw reading this frame vs. the reading that survived stabilisation.
        var raw = frame.Hand.Select(d => d.TileId).ToList();
        var hand = _stabilizer.Push(raw) ?? Array.Empty<int>();

        IReadOnlyList<DiscardOption>? analysis = null;
        if (hand.Count is 13 or 14)
        {
            analysis = DiscardAdvisor.Analyze(hand);
        }

        _handRegion = HandRegionOf(frame.Hand, bitmap.Width, bitmap.Height) ?? _handRegion;

        return new FrameResult(
            bitmap.Width,
            bitmap.Height,
            frame.Detections,
            raw.Count,
            hand,
            analysis,
            frame.Edge,
            frame.Counts,
            frame.Strategy,
            frame.ElapsedMs,
            frame.Attempts.Count);
    }

    /// <summary>Bounding box of the detected hand, padded by half a tile.</summary>
    private static DRect? HandRegionOf(IReadOnlyList<TileDetection> hand, int width, int height)
    {
        if (hand.Count == 0) return null;

        var left = hand.Min(d => d.X);
        var top = hand.Min(d => d.Y);
        var right = hand.Max(d => d.X + d.Width);
        var bottom = hand.Max(d => d.Y + d.Height);
        var padX = Math.Max(12f, (right - left) * 0.06f);
        var padY = Math.Max(12f, (bottom - top) * 0.25f);

        var region = DRect.FromLTRB(
            (int)Math.Floor(left - padX),
            (int)Math.Floor(top - padY),
            (int)Math.Ceiling(right + padX),
            (int)Math.Ceiling(bottom + padY));

        region.Intersect(new DRect(0, 0, width, height));
        return region.Width >= 16 && region.Height >= 16 ? region : null;
    }

    /// <summary>Bottom strip, where a player's own hand almost always sits.</summary>
    private static DRect DefaultHandRegion(int width, int height) =>
        new(0, (int)(height * 0.70), width, Math.Max(1, (int)(height * 0.30)));

    private void Apply(FrameResult frame)
    {
        if (frame.Skipped)
        {
            // Numbers are unchanged; only the status line needs to explain why.
            if (!_skipLogged)
            {
                _skipLogged = true;
                DiagnosticsLog.Write($"static: hand area unchanged, inference skipped ({frame.Hand.Count} tiles held)");
            }

            StatusText.Text = BuildStatus(frame);
            return;
        }

        if (_skipLogged)
        {
            _skipLogged = false;
            DiagnosticsLog.Write($"resumed: screen changed after {_change.Skipped} skipped frame(s)");
        }

        HandText.Text = frame.Hand.Count == 0
            ? "未识别到手牌"
            : FormatHand(frame.Hand);

        var status = BuildStatus(frame);
        StatusText.Text = status;
        if (status != _lastStatus)
        {
            _lastStatus = status;
            DiagnosticsLog.Write(status);
        }

        if (frame.Analysis is { Count: > 0 })
        {
            var top = frame.Analysis[0];
            DiscardText.Text = top.Tile;
            DetailText.Text = $"向听 {top.Shanten} · 进张 {top.Ukeire} 张 · 综合风险 {top.RiskSummary}";
            AlternativesText.Text = "备选：" + string.Join("、",
                frame.Analysis.Skip(1).Take(3).Select(x => $"{x.Tile}（进张 {x.Ukeire}）"));
            _overlay?.UpdateCard(top);
        }
        else
        {
            DiscardText.Text = "—";
            DetailText.Text = frame.Hand.Count == 0
                ? "未识别到手牌，请确认牌桌在前台且完整可见。"
                : $"识别到 {frame.Hand.Count} 张，需要 13 或 14 张才能分析。";
            AlternativesText.Text = string.Empty;
            _overlay?.UpdateCard(null);
        }
    }

    private string BuildStatus(FrameResult frame)
    {
        var head =
            $"画面 {frame.Width}×{frame.Height} · 检测 {frame.Detections} 张 · 手牌 {frame.Hand.Count} 张（{frame.Edge}边）" +
            $" · 边缘 上{frame.Counts.Top}/下{frame.Counts.Bottom}/左{frame.Counts.Left}/右{frame.Counts.Right}";

        if (frame.Skipped)
        {
            return $"{head} · 静止跳过（{_change.Skipped} 帧未变化，省一次推理）";
        }

        return head +
               $" · {frame.Strategy} {frame.ElapsedMs}ms/{frame.Passes}次" +
               (frame.RawCount != frame.Hand.Count ? $" · 本帧直读 {frame.RawCount} 张" : string.Empty);
    }

    /// <summary>
    /// Group the hand by suit so it scans like a real hand: 万 | 筒 | 条 | 字.
    /// </summary>
    private static string FormatHand(IReadOnlyList<int> hand)
    {
        var groups = hand
            .GroupBy(Tiles.Suit)
            .OrderBy(g => g.Key)
            .Select(g => string.Concat(g.OrderBy(t => t).Select(t => Tiles.Names[t])));
        return string.Join("  |  ", groups);
    }

    private void Cleanup()
    {
        _timer.Stop();
        _scanTimer.Stop();
        _stabilizer.Reset();

        if (_source is not null)
        {
            Hotkey.Unregister(_source.Handle);
            _source.RemoveHook(WndProc);
            _source = null;
        }

        _overlay?.Close();
        _detector?.Dispose();
    }

    public sealed class WindowChoice
    {
        public WindowChoice(WindowInfo info) => Info = info;
        public WindowInfo Info { get; }
        public string Display => $"{Info.Title} · {Info.ProcessName} ({Info.ClientWidth}×{Info.ClientHeight})";
    }

    private sealed record FrameResult(
        int Width,
        int Height,
        int Detections,
        int RawCount,
        IReadOnlyList<int> Hand,
        IReadOnlyList<DiscardOption>? Analysis,
        string Edge,
        EdgeCounts Counts,
        string Strategy,
        long ElapsedMs,
        int Passes,
        bool Skipped = false)
    {
        public bool Playable => Hand.Count is 13 or 14;
    }
}
