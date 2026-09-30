using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Limiter;

internal readonly record struct FlowOwner(string Path, int ProcessId);

public sealed class TrafficEngine : IDisposable
{
    private sealed record Packet(byte[] Data, byte[] Address, long Due, string Path, int ProcessId, bool Outbound, TcpSegmentKey? Segment);
    private readonly object _sync = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Dictionary<FlowKey, FlowOwner> _flows = new();
    private Dictionary<FlowKey, FlowOwner> _tcpFlows = new();
    private readonly Dictionary<string, AppUsage> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<int, AppUsage>> _processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly TrafficPacer _pacer = new();
    private sealed record ProcessRule(AppRule Settings, Process Process);
    private readonly Dictionary<string, Dictionary<int, ProcessRule>> _processRules = new(StringComparer.OrdinalIgnoreCase);
    private PriorityQueue<Packet, long> _pending = new();
    private readonly HashSet<TcpSegmentKey> _queuedSegments = new();
    private readonly RuleStore _rules;

    public TrafficEngine() : this(new RuleStore()) { }
    internal TrafficEngine(RuleStore rules) => _rules = rules;
    private nint _flowHandle;
    private nint _networkHandle;
    private volatile bool _running;
    private bool _limiterEnabled = true;
    private bool _blockerEnabled = true;
    private long _blockedPackets;

    internal bool BlockerEnabled { get { lock (_sync) return _blockerEnabled; } }

    internal void SetBlockerEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (_blockerEnabled == enabled) return;
            _blockerEnabled = enabled;
            foreach (var path in _pending.UnorderedItems.Select(item => item.Element.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
                RetimePath(path);
        }
    }

    private bool IsBlocked(string path, int pid, bool outbound)
    {
        if (!_blockerEnabled) return false;
        if (_rules.IsBlocked(path, outbound)) return true;
        var process = GetProcessRule(path, pid)?.Settings;
        return outbound ? process?.BlockUpload == true : process?.BlockDownload == true;
    }

    internal bool LimiterEnabled { get { lock (_sync) return _limiterEnabled; } }

    internal void SetLimiterEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (_limiterEnabled == enabled) return;
            _limiterEnabled = enabled;
            _pacer.Reset();
            foreach (var path in _pending.UnorderedItems.Select(item => item.Element.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
                RetimePath(path);
            _wake.Set();
        }
    }

    private int GetAppLimit(string path, bool outbound) => _limiterEnabled ? _rules.GetLimit(path, outbound) : 0;
    private Task? _flowTask;
    private Task? _networkTask;
    private Task? _sendTask;
    private Task? _tcpTask;
    private long _pendingBytes;
    private long _capturedPackets;
    private long _matchedPackets;
    private long _sendFailures;
    private long _droppedByLimit;
    private long _suppressedRetransmits;
    private int _networkError;
    private long _lastSnapshotTicks;
    private const long MaxPendingBytes = 16 * 1024 * 1024;
    public string Status
    {
        get
        {
            int error = Volatile.Read(ref _networkError);
            if (error != 0) return $"Paket izleme durdu (Windows hata {error})";
            if (!_running) return "İzleme kapalı";
            long failed = Interlocked.Read(ref _sendFailures);
            return $"Canlı izleme · Eşleşen paket: {Interlocked.Read(ref _matchedPackets):N0} / {Interlocked.Read(ref _capturedPackets):N0}" +
                (Interlocked.Read(ref _droppedByLimit) > 0 ? $" · Kuyruk taşması (toplam): {Interlocked.Read(ref _droppedByLimit):N0}" : "") +
                (Interlocked.Read(ref _suppressedRetransmits) > 0 ? $" · Tekrar (toplam): {Interlocked.Read(ref _suppressedRetransmits):N0}" : "") +
                (Interlocked.Read(ref _blockedPackets) > 0 ? $" · Engellenen paket: {Interlocked.Read(ref _blockedPackets):N0}" : "") +
                (failed > 0 ? $" · Gönderme hatası: {failed:N0}" : "");
        }
    }

    public void Start()
    {
        _flowHandle = Native.WinDivertOpen("true", 2, 0, 5); // sniff + recv only
        if (_flowHandle == Native.InvalidHandle)
            throw new InvalidOperationException($"Bağlantı izleyicisi açılamadı (Windows hata {Marshal.GetLastWin32Error()}).");
        _networkHandle = Native.WinDivertOpen("(ip or ipv6) and (tcp or udp) and !loopback", 0, 0, 0);
        if (_networkHandle == Native.InvalidHandle)
        {
            var error = Marshal.GetLastWin32Error();
            Native.WinDivertClose(_flowHandle);
            throw new InvalidOperationException($"Paket izleyicisi açılamadı (Windows hata {error}).");
        }
        _running = true;
        _lastSnapshotTicks = Stopwatch.GetTimestamp();
        _flowTask = Task.Run(FlowLoop);
        _networkTask = Task.Run(NetworkLoop);
        _sendTask = Task.Run(SendLoop);
        _tcpTask = Task.Run(TcpTableLoop);
    }

    public List<AppUsage> Snapshot()
    {
        lock (_sync)
        {
            long now = Stopwatch.GetTimestamp();
            double elapsed = Math.Max(0.05, (now - _lastSnapshotTicks) / (double)Stopwatch.Frequency);
            _lastSnapshotTicks = now;
            double weight = 1 - Math.Exp(-elapsed / 3.0);
            var result = new List<AppUsage>();
            foreach (var app in _apps.Values)
            {
                var rule = _rules.Get(app.Path);
                UpdateRate(app, elapsed, weight);
                var snapshot = CopyUsage(app, rule);
                foreach (var process in _processes[app.Path].Values)
                {
                    UpdateRate(process, elapsed, weight);
                    var own = GetProcessRule(process.Path, process.ProcessId);
                    var child = CopyUsage(process, own?.Settings ?? new AppRule { Path = process.Path });
                    child.ParentRule = rule;
                    child.AppDownloadLimit = rule.DownloadKBps;
                    child.AppUploadLimit = rule.UploadKBps;
                    snapshot.Processes.Add(child);
                }
                result.Add(snapshot);
            }
            return result.OrderByDescending(x => x.DownloadRate + x.UploadRate).ToList();
        }
    }

    private static void UpdateRate(AppUsage usage, double elapsed, double weight)
    {
        double down = (usage.DownloadBytes - usage.LastDownloadBytes) / elapsed;
        double up = (usage.UploadBytes - usage.LastUploadBytes) / elapsed;
        usage.DownloadRate = usage.HasRateSample ? usage.DownloadRate + weight * (down - usage.DownloadRate) : down;
        usage.UploadRate = usage.HasRateSample ? usage.UploadRate + weight * (up - usage.UploadRate) : up;
        usage.HasRateSample = true;
        usage.LastDownloadBytes = usage.DownloadBytes;
        usage.LastUploadBytes = usage.UploadBytes;
    }

    private static AppUsage CopyUsage(AppUsage usage, AppRule rule) => new()
    {
        Path = usage.Path, Name = usage.Name, ProcessId = usage.ProcessId,
        DownloadBytes = usage.DownloadBytes, UploadBytes = usage.UploadBytes,
        DownloadRate = usage.DownloadRate, UploadRate = usage.UploadRate,
        DownloadLimit = rule.DownloadKBps, UploadLimit = rule.UploadKBps,
        DownloadLimitEnabled = rule.DownloadLimitEnabled, UploadLimitEnabled = rule.UploadLimitEnabled,
        BlockDownload = rule.BlockDownload, BlockUpload = rule.BlockUpload
    };

    // Called while holding _sync.
    private void EnsureProcess(string path, int pid)
    {
        if (!_apps.ContainsKey(path))
        {
            _apps[path] = new AppUsage { Path = path, Name = System.IO.Path.GetFileNameWithoutExtension(path) };
            _processes[path] = new Dictionary<int, AppUsage>();
        }
        if (!_processes[path].ContainsKey(pid))
            _processes[path][pid] = new AppUsage { Path = path, Name = _apps[path].Name, ProcessId = pid };
    }

    public void SetLimits(string path, int downloadKBps, int uploadKBps)
    {
        lock (_sync) SetRule(WithLimits(_rules.Get(path), downloadKBps, uploadKBps));
    }

    public void SetProcessLimits(string path, int pid, int downloadKBps, int uploadKBps)
    {
        lock (_sync) SetRule(WithLimits(GetProcessRule(path, pid)?.Settings ?? new AppRule { Path = path }, downloadKBps, uploadKBps), pid);
    }

    private static AppRule WithLimits(AppRule rule, int down, int up) => new()
    {
        Path = rule.Path, DownloadKBps = down, UploadKBps = up,
        DownloadLimitEnabled = rule.DownloadLimitEnabled, UploadLimitEnabled = rule.UploadLimitEnabled,
        BlockDownload = rule.BlockDownload, BlockUpload = rule.BlockUpload
    };

    internal void SetRule(AppRule settings, int pid = 0)
    {
        ValidateLimits(settings.DownloadKBps, settings.UploadKBps);
        lock (_sync)
        {
            if (pid == 0) _rules.Set(settings);
            else
            {
                if (!_processRules.TryGetValue(settings.Path, out var rules)) _processRules[settings.Path] = rules = new();
                if (settings.DownloadKBps == 0 && settings.UploadKBps == 0 && !settings.BlockDownload && !settings.BlockUpload)
                {
                    if (rules.Remove(pid, out var removed)) removed.Process.Dispose();
                }
                else
                {
                    var process = Process.GetProcessById(pid);
                    try
                    {
                        // Pin this lifetime so a reused PID cannot inherit the rule.
                        _ = process.Handle;
                        if (process.HasExited || !string.Equals(process.MainModule?.FileName, settings.Path, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Process kapanmış veya artık bu uygulamaya ait değil.");
                        if (rules.Remove(pid, out var previous)) previous.Process.Dispose();
                        rules[pid] = new ProcessRule(settings, process);
                    }
                    catch { process.Dispose(); throw; }
                }
            }
            RetimePath(settings.Path);
        }
    }

    private static void ValidateLimits(int download, int upload)
    {
        if (download < 0 || upload < 0 || (download > 0 && download < 16) || (upload > 0 && upload < 16))
            throw new ArgumentOutOfRangeException(nameof(download), "0 veya en az 16 KB/sn yazın.");
    }

    private ProcessRule? GetProcessRule(string path, int pid)
    {
        if (!_processRules.TryGetValue(path, out var rules) || !rules.TryGetValue(pid, out var rule)) return null;
        if (!rule.Process.HasExited) return rule;
        rules.Remove(pid);
        rule.Process.Dispose();
        return null;
    }

    private int GetProcessLimit(string path, int pid, bool outbound)
    {
        if (!_limiterEnabled) return 0;
        var rule = GetProcessRule(path, pid);
        return rule is null ? 0 : outbound
            ? (rule.Settings.UploadLimitEnabled ? rule.Settings.UploadKBps : 0)
            : (rule.Settings.DownloadLimitEnabled ? rule.Settings.DownloadKBps : 0);
    }

    private void RetimePath(string path)
    {
        _pacer.ResetReservations(path);
        long now = Stopwatch.GetTimestamp();
        long immediate = now;
        var retained = new PriorityQueue<Packet, long>();
        while (_pending.TryDequeue(out var packet, out long priority))
        {
            if (!string.Equals(packet.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                retained.Enqueue(packet, priority);
                continue;
            }
            if (IsBlocked(path, packet.ProcessId, packet.Outbound))
            {
                RemovePending(packet);
                Interlocked.Increment(ref _blockedPackets);
                continue;
            }
            int appLimit = GetAppLimit(path, packet.Outbound);
            int processLimit = GetProcessLimit(path, packet.ProcessId, packet.Outbound);
            long due = appLimit == 0 && processLimit == 0 ? ++immediate :
                _pacer.Reserve(new FlowOwner(path, packet.ProcessId), packet.Outbound, packet.Data.Length,
                    appLimit, processLimit, now, Stopwatch.Frequency);
            if (due < 0)
            {
                RemovePending(packet);
                Interlocked.Increment(ref _droppedByLimit);
            }
            else retained.Enqueue(packet with { Due = due }, due);
        }
        _pending = retained;
        _wake.Set();
    }
    private void RemovePending(Packet packet)
    {
        _pendingBytes -= packet.Data.Length;
        if (packet.Segment is { } segment) _queuedSegments.Remove(segment);
    }

    private void FlowLoop()
    {
        var address = new byte[Native.AddressSize];
        while (_running)
        {
            if (!Native.WinDivertRecv(_flowHandle, [], 0, out _, address)) break;
            int eventType = address[9];
            int pid = BitConverter.ToInt32(address, 32);
            byte protocol = address[72];
            UInt128 local = PacketInfo.FlowAddress(address.AsSpan(36, 16));
            UInt128 remote = PacketInfo.FlowAddress(address.AsSpan(52, 16));
            if (local == 0 || remote == 0) continue;
            ushort localPort = BitConverter.ToUInt16(address, 68);
            ushort remotePort = BitConverter.ToUInt16(address, 70);
            var key = new FlowKey(local, localPort, remote, remotePort, protocol);
            lock (_sync)
            {
                if (eventType == 2) { _flows.Remove(key); continue; }
                if (eventType != 1 || pid <= 0) continue;
                try
                {
                    using var process = Process.GetProcessById(pid);
                    string path = process.MainModule?.FileName ?? "";
                    if (path.Length == 0) continue;
                    _flows[key] = new FlowOwner(path, pid);
                    EnsureProcess(path, pid);
                }
                catch { /* The process may have exited or be protected. */ }
            }
        }
    }

    private void NetworkLoop()
    {
        var buffer = new byte[65535];
        var address = new byte[Native.AddressSize];
        while (_running)
        {
            if (!Native.WinDivertRecv(_networkHandle, buffer, (uint)buffer.Length, out uint length, address))
            {
                if (_running) Volatile.Write(ref _networkError, Marshal.GetLastWin32Error());
                break;
            }
            if (length == 0) continue;
            Interlocked.Increment(ref _capturedPackets);
            bool outbound = (BitConverter.ToUInt32(address, 8) & (1u << 17)) != 0;
            if (!PacketInfo.TryParse(buffer.AsSpan(0, (int)length), outbound, out var info))
            {
                Send(buffer, (int)length, address);
                continue;
            }
            long due = 0;
            string? matchedPath = null;
            int matchedPid = 0;
            bool dropForLimit = false;
            bool duplicateInQueue = false;
            bool blocked = false;
            lock (_sync)
            {
                if ((_flows.TryGetValue(info.Flow, out var owner) || _tcpFlows.TryGetValue(info.Flow, out owner)) &&
                    _apps.ContainsKey(owner.Path))
                {
                    string path = owner.Path;
                    matchedPath = path;
                    matchedPid = owner.ProcessId;
                    Interlocked.Increment(ref _matchedPackets);
                    int limit = GetAppLimit(path, outbound);
                    int processLimit = GetProcessLimit(path, owner.ProcessId, outbound);
                    blocked = IsBlocked(path, owner.ProcessId, outbound);
                    if (blocked) Interlocked.Increment(ref _blockedPackets);
                    if (!blocked && (limit > 0 || processLimit > 0) && !info.IsTcpControl)
                    {
                        if (info.TcpSegment is { } segment && _queuedSegments.Contains(segment))
                        {
                            duplicateInQueue = true;
                            Interlocked.Increment(ref _suppressedRetransmits);
                        }
                        else
                        {
                            long now = Stopwatch.GetTimestamp();
                            due = _pendingBytes + length > MaxPendingBytes ? -1 :
                                _pacer.Reserve(owner, outbound, (int)length, limit, processLimit, now, Stopwatch.Frequency);
                            // TCP retransmits on overflow; UDP may lose packets.
                            if (due < 0)
                            {
                                dropForLimit = true;
                                Interlocked.Increment(ref _droppedByLimit);
                            }
                        }
                    }
                }
                if (!dropForLimit && !duplicateInQueue && due > 0)
                {
                    byte[] queuedData = buffer.AsSpan(0, (int)length).ToArray();
                    byte[] queuedAddress = (byte[])address.Clone();
                    _pending.Enqueue(new Packet(queuedData, queuedAddress, due, matchedPath!, matchedPid, outbound, info.TcpSegment), due);
                    if (info.TcpSegment is { } segment) _queuedSegments.Add(segment);
                    _pendingBytes += length;
                    _wake.Set();
                    continue;
                }
            }
            if (blocked || dropForLimit || duplicateInQueue) continue;
            Send(buffer, (int)length, address, matchedPath, outbound, matchedPid);
        }
    }

    private void TcpTableLoop()
    {
        var pidPaths = new Dictionary<int, string>();
        while (_running)
        {
            try
            {
                var table = new Dictionary<FlowKey, FlowOwner>();
                var activePids = new HashSet<int>();
                ReadTcpTable(2, table, pidPaths, activePids);
                ReadTcpTable(23, table, pidPaths, activePids);
                foreach (int pid in pidPaths.Keys.Where(pid => !activePids.Contains(pid)).ToArray()) pidPaths.Remove(pid);
                lock (_sync) _tcpFlows = table;
            }
            catch { /* FLOW events remain the primary source. */ }
            Thread.Sleep(500);
        }
    }

    internal void ReadTcpTable(int family, Dictionary<FlowKey, FlowOwner> table,
        Dictionary<int, string>? pidPaths = null, HashSet<int>? activePids = null)
    {
        uint size = 0;
        Native.GetExtendedTcpTable(0, ref size, false, family, 5, 0);
        if (size < 4 || size > 16 * 1024 * 1024) return;
        nint memory = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.GetExtendedTcpTable(memory, ref size, false, family, 5, 0) != 0) return;
            int count = Marshal.ReadInt32(memory);
            int rowSize = family == 2 ? 24 : 56;
            int addressSize = family == 2 ? 4 : 16;
            if (count < 0 || count > (size - 4) / rowSize) return;
            for (int i = 0; i < count; i++)
            {
                int offset = 4 + i * rowSize;
                var row = new byte[rowSize];
                Marshal.Copy(memory + offset, row, 0, rowSize);
                int pid = BitConverter.ToInt32(row, family == 2 ? 20 : 52);
                if (pid <= 0) continue;
                activePids?.Add(pid);
                UInt128 local, remote;
                ushort localPort, remotePort;
                if (family == 2)
                {
                    local = PacketInfo.MappedV4(row.AsSpan(4, 4));
                    remote = PacketInfo.MappedV4(row.AsSpan(12, 4));
                    localPort = (ushort)((row[8] << 8) | row[9]);
                    remotePort = (ushort)((row[16] << 8) | row[17]);
                }
                else
                {
                    local = PacketInfo.Address(row.AsSpan(0, addressSize));
                    remote = PacketInfo.Address(row.AsSpan(24, addressSize));
                    localPort = (ushort)((row[20] << 8) | row[21]);
                    remotePort = (ushort)((row[44] << 8) | row[45]);
                }
                if (remotePort == 0) continue;
                string path;
                if (pidPaths?.TryGetValue(pid, out path!) != true)
                {
                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        path = process.MainModule?.FileName ?? "";
                    }
                    catch { continue; }
                    if (path.Length == 0) continue;
                    pidPaths?.Add(pid, path);
                }
                table[new FlowKey(local, localPort, remote, remotePort, 6)] = new FlowOwner(path, pid);
                lock (_sync) EnsureProcess(path, pid);
            }
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private void SendLoop()
    {
        while (true)
        {
            Packet? packet = null;
            int waitMs = 1000;
            lock (_sync)
            {
                if (!_running && _pending.Count == 0) break;
                if (_pending.TryPeek(out _, out long due))
                {
                    long remaining = due - Stopwatch.GetTimestamp();
                    if (!_running || remaining <= 0)
                    {
                        packet = _pending.Dequeue();
                        if (IsBlocked(packet.Path, packet.ProcessId, packet.Outbound))
                        {
                            RemovePending(packet);
                            Interlocked.Increment(ref _blockedPackets);
                            continue;
                        }
                        var owner = new FlowOwner(packet.Path, packet.ProcessId);
                        int appLimit = GetAppLimit(packet.Path, packet.Outbound);
                        int processLimit = GetProcessLimit(packet.Path, packet.ProcessId, packet.Outbound);
                        long now = Stopwatch.GetTimestamp();
                        long ready = _running ? _pacer.ReadyAt(owner, packet.Outbound, packet.Data.Length,
                            appLimit, processLimit, now, Stopwatch.Frequency) : now;
                        if (ready > now)
                        {
                            _pending.Enqueue(packet with { Due = ready }, ready);
                            packet = null;
                            waitMs = 1;
                        }
                        else
                        {
                            _pacer.MarkSent(owner, packet.Outbound, packet.Data.Length, appLimit, processLimit, now, Stopwatch.Frequency);
                            _pendingBytes -= packet.Data.Length;
                        }
                    }
                    else waitMs = Math.Clamp((int)Math.Ceiling(remaining * 1000.0 / Stopwatch.Frequency), 1, 1000);
                }
            }
            if (packet is null) _wake.WaitOne(waitMs);
            else
            {
                Send(packet.Data, packet.Data.Length, packet.Address, packet.Path, packet.Outbound, packet.ProcessId);
                if (packet.Segment is { } segment)
                    lock (_sync) _queuedSegments.Remove(segment);
            }
        }
    }

    private void Send(byte[] packet, int packetLength, byte[] address, string? path = null, bool outbound = false, int processId = 0)
    {
        if (!Native.WinDivertSend(_networkHandle, packet, (uint)packetLength, out uint sent, address))
            Interlocked.Increment(ref _sendFailures);
        else if (path is not null)
            RecordSent(new FlowOwner(path, processId), sent, outbound);
    }

    internal void RecordSent(FlowOwner owner, uint bytes, bool outbound)
    {
        lock (_sync)
        {
            if (!_apps.TryGetValue(owner.Path, out var app)) return;
            if (outbound) app.UploadBytes += bytes;
            else app.DownloadBytes += bytes;
            if (_processes[owner.Path].TryGetValue(owner.ProcessId, out var process))
            {
                if (outbound) process.UploadBytes += bytes;
                else process.DownloadBytes += bytes;
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        if (_flowHandle != 0 && _flowHandle != Native.InvalidHandle) Native.WinDivertShutdown(_flowHandle, 1);
        if (_networkHandle != 0 && _networkHandle != Native.InvalidHandle) Native.WinDivertShutdown(_networkHandle, 1);
        try { _flowTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _networkTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _tcpTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _sendTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        if (_flowHandle != 0 && _flowHandle != Native.InvalidHandle) Native.WinDivertClose(_flowHandle);
        if (_networkHandle != 0 && _networkHandle != Native.InvalidHandle) Native.WinDivertClose(_networkHandle);
        lock (_sync)
        {
            foreach (var rules in _processRules.Values)
                foreach (var rule in rules.Values) rule.Process.Dispose();
            _processRules.Clear();
        }
    }
}
