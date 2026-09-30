namespace Limiter;

internal readonly record struct PacingScope(string Path, int ProcessId, bool Outbound);

internal sealed class TrafficPacer
{
    private readonly Dictionary<PacingScope, long> _reserved = new();
    private readonly Dictionary<PacingScope, long> _sent = new();

    internal long Reserve(FlowOwner owner, bool outbound, int bytes, int appLimit, int processLimit, long now, long frequency)
    {
        var app = new PacingScope(owner.Path, 0, outbound);
        var process = new PacingScope(owner.Path, owner.ProcessId, outbound);
        long appDue = appLimit > 0 ? RateMath.Due(now, _reserved.GetValueOrDefault(app), bytes, appLimit, frequency) : now;
        long processDue = processLimit > 0 ? RateMath.Due(now, _reserved.GetValueOrDefault(process), bytes, processLimit, frequency) : now;
        long due = Math.Max(appDue, processDue);
        if (due - now > 10 * frequency) return -1;
        if (appLimit > 0) _reserved[app] = appDue;
        if (processLimit > 0) _reserved[process] = processDue;
        return due;
    }

    // Bound bursts to 10 ms (or one packet), while allowing timer jitter to catch up.
    internal long ReadyAt(FlowOwner owner, bool outbound, int bytes, int appLimit, int processLimit, long now, long frequency)
    {
        long due = now;
        if (appLimit > 0)
            due = Math.Max(due, AllowedAt(new(owner.Path, 0, outbound), bytes, appLimit, now, frequency));
        if (processLimit > 0)
            due = Math.Max(due, AllowedAt(new(owner.Path, owner.ProcessId, outbound), bytes, processLimit, now, frequency));
        return due;
    }

    private long AllowedAt(PacingScope scope, int bytes, int limit, long now, long frequency)
    {
        long interval = RateMath.Due(0, 0, bytes, limit, frequency);
        long finish = RateMath.Due(now, _sent.GetValueOrDefault(scope), bytes, limit, frequency);
        return finish - Math.Max(interval, frequency / 100);
    }

    internal void MarkSent(FlowOwner owner, bool outbound, int bytes, int appLimit, int processLimit, long now, long frequency)
    {
        var app = new PacingScope(owner.Path, 0, outbound);
        var process = new PacingScope(owner.Path, owner.ProcessId, outbound);
        if (appLimit > 0) _sent[app] = RateMath.Due(now, _sent.GetValueOrDefault(app), bytes, appLimit, frequency);
        if (processLimit > 0) _sent[process] = RateMath.Due(now, _sent.GetValueOrDefault(process), bytes, processLimit, frequency);
    }

    internal void Reset()
    {
        _reserved.Clear();
        _sent.Clear();
    }

    internal void ResetReservations(string path)
    {
        foreach (var key in _reserved.Keys.Where(key => string.Equals(key.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray())
            _reserved.Remove(key);
    }
}
