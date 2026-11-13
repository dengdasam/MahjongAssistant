using Mahjong.Core;

namespace Mahjong.Vision;

/// <summary>
/// Damps single-frame misreads without adding start-up latency:
/// the first result is shown immediately, but a *different* hand only replaces it
/// after appearing <see cref="RequiredHits"/> frames in a row. An empty reading
/// clears straight away so a stale suggestion is never left on screen.
/// </summary>
public sealed class HandStabilizer
{
    private readonly int _requiredHits;
    private IReadOnlyList<int>? _shown;
    private IReadOnlyList<int>? _pending;
    private int _pendingHits;

    public HandStabilizer(int requiredHits = 2) => _requiredHits = Math.Max(1, requiredHits);

    public int RequiredHits => _requiredHits;

    /// <summary>The hand currently safe to display, or null when nothing is known.</summary>
    public IReadOnlyList<int>? Shown => _shown;

    /// <summary>
    /// True while a different hand is waiting for its second sighting. Callers that
    /// skip frames must not skip while this is set, or the change never gets confirmed.
    /// </summary>
    public bool HasPending => _pending is not null;

    public IReadOnlyList<int>? Push(IReadOnlyList<int> observed)
    {
        if (observed.Count == 0)
        {
            Reset();
            return null;
        }

        if (_shown is not null && Same(_shown, observed))
        {
            _pending = null;
            _pendingHits = 0;
            return _shown;
        }

        if (_shown is null)
        {
            _shown = observed;
            _pending = null;
            _pendingHits = 0;
            return _shown;
        }

        if (_pending is not null && Same(_pending, observed))
        {
            _pendingHits++;
        }
        else
        {
            _pending = observed;
            _pendingHits = 1;
        }

        if (_pendingHits >= _requiredHits)
        {
            _shown = observed;
            _pending = null;
            _pendingHits = 0;
        }

        return _shown;
    }

    public void Reset()
    {
        _shown = null;
        _pending = null;
        _pendingHits = 0;
    }

    private static bool Same(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
