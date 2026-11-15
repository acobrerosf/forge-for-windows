namespace Forge.Core.Api;

/// <summary>
/// Forge allows 60 requests a minute per Forge *user*, shared with their
/// browser. The ledger is keyed by that identity, not by account name, and
/// keeps a deliberate margin under the limit. A 429 holds the whole bucket.
/// </summary>
public sealed class RateBudget
{
    public const int DefaultCeiling = 40;
    private const long WindowMs = 60_000;

    private readonly object _lock = new();
    private readonly Dictionary<string, Queue<long>> _spent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _heldUntil = new(StringComparer.Ordinal);

    public int Ceiling { get; init; } = DefaultCeiling;

    private Queue<long> Window(string bucket, long nowMs)
    {
        if (!_spent.TryGetValue(bucket, out var q)) _spent[bucket] = q = new Queue<long>();
        while (q.Count > 0 && nowMs - q.Peek() >= WindowMs) q.Dequeue();
        return q;
    }

    public int Spent(string bucket, long nowMs)
    {
        lock (_lock) return Window(bucket, nowMs).Count;
    }

    public int Headroom(string bucket, long nowMs)
    {
        lock (_lock) return Math.Max(0, Ceiling - Window(bucket, nowMs).Count);
    }

    /// <summary>
    /// Charges one request. <paramref name="force"/> is for the first page of
    /// a server list, which is never dropped because the tray icon judges it.
    /// </summary>
    public bool TryCharge(string bucket, long nowMs, bool force = false)
    {
        lock (_lock)
        {
            if (HeldUntilLocked(bucket, nowMs) > 0) return false;
            var q = Window(bucket, nowMs);
            if (!force && q.Count >= Ceiling) return false;
            q.Enqueue(nowMs);
            return true;
        }
    }

    /// <summary>A request that never left the machine is given back.</summary>
    public void Refund(string bucket)
    {
        lock (_lock)
        {
            if (!_spent.TryGetValue(bucket, out var q) || q.Count == 0) return;
            var kept = q.ToArray()[..^1];
            q.Clear();
            foreach (var t in kept) q.Enqueue(t);
        }
    }

    public void Hold(string bucket, long untilMs)
    {
        lock (_lock)
        {
            _heldUntil[bucket] = Math.Max(untilMs, _heldUntil.GetValueOrDefault(bucket));
        }
    }

    /// <summary>When the hold on this bucket ends, or 0 when it is not held.</summary>
    public long HeldUntil(string bucket, long nowMs)
    {
        lock (_lock) return HeldUntilLocked(bucket, nowMs);
    }

    private long HeldUntilLocked(string bucket, long nowMs)
    {
        if (!_heldUntil.TryGetValue(bucket, out var until)) return 0;
        if (until > nowMs) return until;
        _heldUntil.Remove(bucket);
        return 0;
    }
}
