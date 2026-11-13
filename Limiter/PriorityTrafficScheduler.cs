namespace Limiter;

// All access is serialized by TrafficEngine._sync. Only matched data packets enter
// this scheduler; TCP control packets retain the engine's existing bypass.
internal sealed class PriorityTrafficScheduler
{
    private sealed class AppQueue
    {
        internal readonly PriorityQueue<TrafficEngine.Packet, long> Packets = new();
        internal int Weight;
        internal double Finish;
    }

    private sealed class Direction
    {
        internal readonly Dictionary<string, AppQueue> Apps = new(StringComparer.OrdinalIgnoreCase);
        internal int Capacity;
        internal long Sent;
        internal double VirtualFinish;
    }

    private readonly Direction _download = new();
    private readonly Direction _upload = new();
    private bool _enabled;
    private bool _preferOutbound;

    internal bool IsEnabled(bool outbound) => _enabled && GetDirection(outbound).Capacity > 0;

    internal void SetEnabled(bool enabled) => _enabled = enabled;

    internal void SetCapacity(int downloadKBps, int uploadKBps)
    {
        SetCapacity(_download, downloadKBps);
        SetCapacity(_upload, uploadKBps);
    }

    private static void SetCapacity(Direction direction, int capacity)
    {
        if (direction.Capacity == capacity) return;
        direction.Capacity = capacity;
        direction.Sent = 0;
    }

    internal void Enqueue(TrafficEngine.Packet packet, int priority)
    {
        var direction = GetDirection(packet.Outbound);
        if (!direction.Apps.TryGetValue(packet.Path, out var app))
        {
            app = new AppQueue { Finish = direction.VirtualFinish };
            direction.Apps.Add(packet.Path, app);
        }
        // Positive byte weights, not packet counts: low traffic always makes
        // progress and applications cannot gain shares by creating more PIDs.
        app.Weight = 1 << Math.Clamp(priority, 0, 2);
        app.Packets.Enqueue(packet, packet.Due);
    }

    internal bool TryPeek(long now, long frequency, out TrafficEngine.Packet? packet, out long due)
    {
        bool hasDownload = TryPeek(_download, now, frequency, out var download, out long downloadDue);
        bool hasUpload = TryPeek(_upload, now, frequency, out var upload, out long uploadDue);
        bool chooseUpload = hasUpload && (!hasDownload ||
            (downloadDue <= now && uploadDue <= now ? _preferOutbound : uploadDue < downloadDue));
        packet = chooseUpload ? upload : download;
        due = chooseUpload ? uploadDue : downloadDue;
        return hasDownload || hasUpload;
    }

    private static bool TryPeek(Direction direction, long now, long frequency,
        out TrafficEngine.Packet? packet, out long due)
    {
        packet = null;
        due = long.MaxValue;
        double bestFinish = double.PositiveInfinity;
        foreach (var app in direction.Apps.Values)
        {
            if (!app.Packets.TryPeek(out var head, out long headDue)) continue;
            // Gate the whole link, not each packet size: otherwise a stream
            // of small packets could always fit while large packets starve.
            long ready = Math.Max(headDue, direction.Sent - frequency / 100);
            double finish = Math.Max(app.Finish, direction.VirtualFinish) + (double)head.Data.Length / app.Weight;
            if (ready <= now)
            {
                if (due <= now && finish >= bestFinish) continue;
                bestFinish = finish;
            }
            else if (ready >= due) continue;
            packet = head;
            due = ready;
        }
        return packet is not null;
    }

    internal void Defer(TrafficEngine.Packet packet, long due)
    {
        var app = GetDirection(packet.Outbound).Apps[packet.Path];
        app.Packets.Dequeue();
        app.Packets.Enqueue(packet with { Due = due }, due);
    }

    internal void Dequeue(TrafficEngine.Packet packet, long now, long frequency, bool sent)
    {
        var direction = GetDirection(packet.Outbound);
        var app = direction.Apps[packet.Path];
        app.Packets.Dequeue();
        if (sent)
        {
            app.Finish = Math.Max(app.Finish, direction.VirtualFinish) + (double)packet.Data.Length / app.Weight;
            direction.Sent = RateMath.Due(now, direction.Sent, packet.Data.Length, direction.Capacity, frequency);
            _preferOutbound = !packet.Outbound;
        }
        // Retain an emptied application's finish while competitors are queued:
        // otherwise one-packet bursts could repeatedly rejoin with fresh credit.
        if (!sent)
        {
            if (app.Packets.Count == 0) direction.Apps.Remove(packet.Path);
            return;
        }

        double minimum = double.PositiveInfinity;
        bool hasBacklog = false;
        foreach (var other in direction.Apps.Values)
        {
            if (!other.Packets.TryPeek(out _, out long due)) continue;
            hasBacklog = true;
            if (due <= now) minimum = Math.Min(minimum, other.Finish);
        }
        direction.VirtualFinish = Math.Max(direction.VirtualFinish,
            double.IsPositiveInfinity(minimum) ? app.Finish : minimum);
        if (!hasBacklog) direction.Apps.Clear();
    }

    // Configuration/rule changes retime through the original app/PID pacer.
    // Moving queues does not change byte accounting or retransmit suppression.
    internal void DrainTo(PriorityQueue<TrafficEngine.Packet, long> destination)
    {
        DrainTo(_download, destination);
        DrainTo(_upload, destination);
    }

    private static void DrainTo(Direction direction, PriorityQueue<TrafficEngine.Packet, long> destination)
    {
        foreach (var app in direction.Apps.Values)
            while (app.Packets.TryDequeue(out var packet, out long due)) destination.Enqueue(packet, due);
        direction.Apps.Clear();
        direction.VirtualFinish = 0;
    }

    private Direction GetDirection(bool outbound) => outbound ? _upload : _download;
}
