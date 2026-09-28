using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Limiter;

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static byte[] TcpPacket(bool inbound, int payloadLength)
{
    var packet = new byte[40 + payloadLength];
    packet[0] = 0x45;
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)packet.Length);
    packet[9] = 6;
    var local = new byte[] { 192, 168, 1, 3 };
    var remote = new byte[] { 203, 0, 113, 7 };
    (inbound ? remote : local).CopyTo(packet, 12);
    (inbound ? local : remote).CopyTo(packet, 16);
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), (ushort)(inbound ? 443 : 50000));
    BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), (ushort)(inbound ? 50000 : 443));
    BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(24, 4), 123456);
    packet[32] = 0x50;
    packet[33] = 0x10;
    return packet;
}

var inbound = TcpPacket(true, 1200);
Require(PacketInfo.TryParse(inbound, false, out var incoming), "Inbound TCP could not be parsed.");
var expected = new FlowKey(PacketInfo.MappedV4(new byte[] { 192, 168, 1, 3 }), 50000,
    PacketInfo.MappedV4(new byte[] { 203, 0, 113, 7 }), 443, 6);
Require(incoming.Flow == expected && incoming.TcpSegment is { PayloadLength: 1200 }, "Inbound flow or segment mismatch.");
Require(PacketInfo.TryParse(TcpPacket(false, 0), true, out var outbound) && outbound.Flow == expected && outbound.IsTcpControl,
    "Outbound control packet mismatch.");
Require(PacketInfo.TryParse(inbound, false, out var retransmit) && retransmit.TcpSegment == incoming.TcpSegment,
    "Retransmission identity mismatch.");
var nativeAddress = new byte[16];
Require(Native.WinDivertHelperParseIPv6Address("::ffff:192.168.1.3", nativeAddress), "Native IPv4 mapping failed.");
Require(PacketInfo.FlowAddress(nativeAddress) == expected.Local, "Native FLOW address and packet address differ.");

const long frequency = 1_000_000;
var isolatedPacer = new TrafficPacer();
var firstPid = new FlowOwner("test.exe", 101);
var secondPid = new FlowOwner("test.exe", 202);
long firstDue = isolatedPacer.Reserve(firstPid, false, 1024, 0, 16, 0, frequency);
long secondDue = isolatedPacer.Reserve(secondPid, false, 1024, 0, 0, 0, frequency);
Require(firstDue == 62500 && secondDue == 0, "PID rule delayed a sibling process.");
Require(isolatedPacer.Reserve(firstPid, true, 1024, 0, 0, 0, frequency) == 0,
    "Download PID rule affected upload.");

var combinedPacer = new TrafficPacer();
var queue = new PriorityQueue<(FlowOwner Owner, int Limit), long>();
const long start = 1_000_000_000;
for (int i = 0; i < 40; i++)
{
    queue.Enqueue((firstPid, 16), combinedPacer.Reserve(firstPid, false, 1024, 128, 16, start, frequency));
    queue.Enqueue((secondPid, 64), combinedPacer.Reserve(secondPid, false, 1024, 128, 64, start, frequency));
}
var counts = new Dictionary<int, int>();
var firstSent = new Dictionary<int, long>();
var lastSent = new Dictionary<int, long>();
var sentEvents = new List<long>();
while (queue.TryDequeue(out var packet, out long at))
{
    long ready = combinedPacer.ReadyAt(packet.Owner, false, 1024, 128, packet.Limit, at, frequency);
    if (ready > at) { queue.Enqueue(packet, ready); continue; }
    combinedPacer.MarkSent(packet.Owner, false, 1024, 128, packet.Limit, at, frequency);
    sentEvents.Add(at);
    firstSent.TryAdd(packet.Owner.ProcessId, at);
    lastSent[packet.Owner.ProcessId] = at;
    counts[packet.Owner.ProcessId] = counts.GetValueOrDefault(packet.Owner.ProcessId) + 1;
}
Require(counts[101] == 40 && counts[202] == 40, "Combined pacing lost packets.");
Require(lastSent[202] < lastSent[101], "Slow PID blocked its faster sibling.");
for (int i = 0; i < sentEvents.Count; i++)
{
    int end = i;
    while (end < sentEvents.Count && sentEvents[end] - sentEvents[i] <= frequency / 10) end++;
    Require(end - i <= 15, "Combined traffic exceeded aggregate cap plus its bounded burst allowance.");
}
foreach (var pair in new[] { (Pid: 101, Limit: 16), (Pid: 202, Limit: 64) })
{
    double rate = (counts[pair.Pid] - 1) * frequency / (double)(lastSent[pair.Pid] - firstSent[pair.Pid]);
    Require(rate <= pair.Limit * 1.02, "PID pacing exceeded its individual rate.");
}
Console.WriteLine("PID isolation, upload separation and combined application limits: OK");
foreach (int limit in new[] { 16, 500, 1024 })
{
    long due = 0;
    for (int i = 0; i < 10_000; i++) due = RateMath.Due(0, due, 1500, limit, frequency);
    double rate = 15_000_000.0 * frequency / due / 1024;
    Require(Math.Abs(rate - limit) / limit < 0.001, $"Pacing rate mismatch for {limit} KB/s: {rate:0.0}");
    Console.WriteLine($"Pacing {limit} KB/s -> {rate:0.00} KB/s");
}

using (var listener = new TcpListener(IPAddress.Loopback, 0))
{
    listener.Start();
    int serverPort = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var client = new TcpClient();
    var acceptTask = listener.AcceptTcpClientAsync();
    client.Connect(IPAddress.Loopback, serverPort);
    using var accepted = await acceptTask;
    int clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    using var engine = new TrafficEngine();
    var table = new Dictionary<FlowKey, FlowOwner>();
    engine.ReadTcpTable(2, table);
    var loopback = PacketInfo.MappedV4(new byte[] { 127, 0, 0, 1 });
    Require(table.ContainsKey(new FlowKey(loopback, (ushort)clientPort, loopback, (ushort)serverPort, 6)),
        "Windows TCP owner table did not match a live connection.");
    Require(table[new FlowKey(loopback, (ushort)clientPort, loopback, (ushort)serverPort, 6)].ProcessId == Environment.ProcessId,
        "Windows TCP owner table returned an incorrect PID.");
    Console.WriteLine("Live Windows TCP owner table: OK");
    var owner = table[new FlowKey(loopback, (ushort)clientPort, loopback, (ushort)serverPort, 6)];
    engine.RecordSent(owner, 1500, false);
    engine.RecordSent(owner, 320, true);
    var usage = engine.Snapshot().Single(app => app.Path == owner.Path);
    Require(usage.Processes.Any(process => process.ProcessId == Environment.ProcessId), "PID child is missing.");
    Require(usage.DownloadBytes == 1500 && usage.UploadBytes == 320 &&
        usage.Processes.Sum(process => process.DownloadBytes) == usage.DownloadBytes &&
        usage.Processes.Sum(process => process.UploadBytes) == usage.UploadBytes,
        "Process totals differ from application totals.");
    Require(Math.Abs(usage.Processes.Sum(process => process.DownloadRate) - usage.DownloadRate) < 0.001,
        "Process rate differs from application rate.");
    Console.WriteLine("Application and PID traffic totals: OK");
    int originalAppDown = usage.DownloadLimit;
    engine.SetProcessLimits(owner.Path, owner.ProcessId, 64, 0);
    var limited = engine.Snapshot().Single(app => app.Path == owner.Path);
    Require(limited.DownloadLimit == originalAppDown &&
        limited.Processes.Single(process => process.ProcessId == owner.ProcessId).DownloadLimit == 64,
        "PID rule changed the application rule.");
    engine.SetProcessLimits(owner.Path, owner.ProcessId, 0, 0);
    Require(engine.Snapshot().Single(app => app.Path == owner.Path).Processes
        .Single(process => process.ProcessId == owner.ProcessId).DownloadLimit == 0,
        "PID rule could not be removed independently.");
    Console.WriteLine("Live PID rule creation and independent removal: OK");
    var tableWatch = Stopwatch.StartNew();
    for (int i = 0; i < 20; i++) { table.Clear(); engine.ReadTcpTable(2, table); }
    tableWatch.Stop();
    var pathCache = new Dictionary<int, string>();
    tableWatch.Restart();
    for (int i = 0; i < 20; i++) { table.Clear(); engine.ReadTcpTable(2, table, pathCache); }
    tableWatch.Stop();
    Console.WriteLine($"20 cached TCP table scans: {tableWatch.ElapsedMilliseconds} ms");
}

for (int i = 0; i < 10_000; i++) PacketInfo.TryParse(inbound, false, out _);
long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
var watch = Stopwatch.StartNew();
for (int i = 0; i < 100_000; i++) PacketInfo.TryParse(inbound, false, out _);
watch.Stop();
long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
Require(allocated < 1024, $"Packet parsing allocated {allocated} bytes for 100,000 packets.");
Console.WriteLine($"100,000 packet parses: {watch.ElapsedMilliseconds} ms, {allocated} bytes allocated");
