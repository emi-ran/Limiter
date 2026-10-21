using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using Limiter;

if (args.Contains("--live-blocker"))
{
    await LiveBlockerChecks.Run();
    return;
}

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

// Windows ACL policy regression cases require no elevated writes to Program Files.
static void CheckStartupSecurity()
{
    static DirectorySecurity Security(string sddl, params int[] expectedMasks)
    {
        // Raw SDDL preserves input order; DirectorySecurity may canonicalize ACE order.
        var descriptor = new RawSecurityDescriptor(sddl);
        if (descriptor.DiscretionaryAcl is null)
            Require(expectedMasks.Length == 0, "Missing fixture DACL: " + sddl);
        else
        {
            Require(descriptor.DiscretionaryAcl.Count == expectedMasks.Length, "Fixture ACE count mismatch: " + sddl);
            for (int i = 0; i < expectedMasks.Length; i++)
            {
                var ace = (KnownAce)descriptor.DiscretionaryAcl[i];
                Require(ace.AccessMask == expectedMasks[i],
                    $"Fixture AccessMask mismatch at input ACE {i}: expected identity={ace.SecurityIdentifier} mask=0x{expectedMasks[i]:x8}; " +
                    $"actual identity={ace.SecurityIdentifier} mask=0x{ace.AccessMask:x8}: {sddl}");
            }
        }

        static string AceIdentity(GenericAce ace)
        {
            var known = (KnownAce)ace;
            return $"{known.SecurityIdentifier} mask=0x{known.AccessMask:x8} flags={ace.AceFlags} type={ace.AceType}";
        }
        static string[] AceMultiset(RawAcl? acl) => acl is null ? Array.Empty<string>() :
            acl.Cast<GenericAce>().Select(AceIdentity).OrderBy(identity => identity, StringComparer.Ordinal).ToArray();

        var expectedAces = AceMultiset(descriptor.DiscretionaryAcl);
        var result = new DirectorySecurity();
        result.SetSecurityDescriptorSddlForm(sddl);
        var normalized = new RawSecurityDescriptor(result.GetSecurityDescriptorBinaryForm(), 0);
        var actualAces = AceMultiset(normalized.DiscretionaryAcl);
        // Compare whole ACE identities, not positional masks or a set that loses duplicates.
        Require((descriptor.DiscretionaryAcl is null) == (normalized.DiscretionaryAcl is null) &&
            expectedAces.SequenceEqual(actualAces),
            $"Fixture normalization changed ACEs: expected identity/mask/flags=[{string.Join("; ", expectedAces)}]; " +
            $"actual identity/mask/flags=[{string.Join("; ", actualAces)}]: {sddl}");
        return result;
    }
    // All fixture masks use explicit file-right bits, independently checked against FileSystemRights.
    const int fullControl = (int)FileSystemRights.FullControl;
    const int readExecute = (int)FileSystemRights.ReadAndExecute | (int)FileSystemRights.Synchronize;
    Require(StartupRegistration.IsProtectedSecurity(Security(
        "O:BAG:BAD:P(A;;0x1f01ff;;;BA)(A;;0x1f01ff;;;SY)(A;;0x1200a9;;;BU)", fullControl, fullControl, readExecute)),
        "Administrator-owned read-only installation was rejected.");
    foreach (var unsafeAcl in new (string Sddl, int Mask)[] {
        ("O:BAG:BAD:P(A;;0x1f01ff;;;WD)", fullControl), // Everyone full control (WD here is a trustee)
        ("O:BAG:BAD:P(A;;0x2;;;AU)", (int)FileSystemRights.WriteData), // Authenticated Users write data / add files
        ("O:BAG:BAD:P(A;;0x2;;;BU)", (int)FileSystemRights.WriteData), // Users write data / add files
        ("O:BAG:BAD:P(A;;0x40;;;BU)", (int)FileSystemRights.DeleteSubdirectoriesAndFiles), // delete children
        ("O:BAG:BAD:P(A;OICIIO;0x40000000;;;BU)", 0x40000000), // unmapped GENERIC_WRITE, inherit-only
        ("O:BUG:BAD:P(A;;0x1200a9;;;BU)", readExecute) // user-controlled owner
    })
        Require(!StartupRegistration.IsProtectedSecurity(Security(unsafeAcl.Sddl, unsafeAcl.Mask)),
            "Unsafe startup ACL accepted: " + unsafeAcl.Sddl);
    Require(!StartupRegistration.IsProtectedSecurity(Security("O:BAG:BAD:NO_ACCESS_CONTROL")),
        "Null DACL accepted.");
    // DC is ADS_RIGHT_DS_DELETE_CHILD (0x2), which is WriteData/AddFile for files, NOT FILE_DELETE_CHILD (0x40).
    var dcSecurity = Security("O:BAG:BAD:P(A;;DC;;;BU)", (int)FileSystemRights.WriteData);
    Require(!StartupRegistration.IsProtectedSecurity(dcSecurity), "WriteData inside installation accepted.");
    Require(StartupRegistration.IsProtectedSecurity(dcSecurity, ancestor: true), "Ancestor AddFile confused with delete-child.");
    // WD in the rights field means WRITE_DAC (0x40000), not WriteData.
    var wdSecurity = Security("O:BAG:BAD:P(A;;WD;;;BU)", (int)FileSystemRights.ChangePermissions);
    Require(!StartupRegistration.IsProtectedSecurity(wdSecurity, ancestor: true), "Ancestor WRITE_DAC accepted.");
    // Program Files commonly grants CREATOR OWNER full control only to descendants.
    const string safeAncestor = "O:BAG:BAD:P(A;OICI;0x1f01ff;;;BA)(A;OICI;0x1f01ff;;;SY)(A;OICI;0x1200a9;;;BU)(A;OICIIO;0x1f01ff;;;CO)";
    var safeAncestorSecurity = Security(safeAncestor, fullControl, fullControl, readExecute, fullControl);
    Require(StartupRegistration.IsProtectedSecurity(safeAncestorSecurity, ancestor: true),
        "Safe Program Files ancestor with inherit-only CREATOR OWNER grant was rejected.");
    Require(!StartupRegistration.IsProtectedSecurity(safeAncestorSecurity),
        "Inherit-only mutation grant inside the installation was accepted.");
    foreach (var unsafeAncestor in new (string Sddl, int Mask)[] {
        ("O:BAG:BAD:P(A;;0x40;;;BU)", (int)FileSystemRights.DeleteSubdirectoriesAndFiles), // delete children
        ("O:BAG:BAD:P(A;;0x10000;;;BU)", (int)FileSystemRights.Delete), // delete this ancestor
        ("O:BAG:BAD:P(A;;0x40000;;;BU)", (int)FileSystemRights.ChangePermissions), // change DACL
        ("O:BAG:BAD:P(A;;0x80000;;;BU)", (int)FileSystemRights.TakeOwnership), // take ownership
        ("O:BAG:BAD:P(A;;0x10000000;;;BU)", 0x10000000) // unmapped GENERIC_ALL includes replacement rights
    })
        Require(!StartupRegistration.IsProtectedSecurity(Security(unsafeAncestor.Sddl, unsafeAncestor.Mask), ancestor: true),
            "Unsafe effective ancestor rights accepted: " + unsafeAncestor.Sddl);
    Require(!StartupRegistration.HasProtectedInstallFiles(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Guid.NewGuid().ToString(), "Limiter.exe")),
        "Missing installation passed actual protection checks.");
    string root = Path.Combine(Path.GetTempPath(), "Limiter-security-" + Guid.NewGuid());
    Directory.CreateDirectory(root);
    try
    {
        Require(!StartupRegistration.IsProtectedEntry(new DirectoryInfo(root)), "User-writable directory accepted.");
        string target = Path.Combine(root, "target with spaces"), junction = Path.Combine(root, "junction with spaces");
        Directory.CreateDirectory(target);
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = $"/d /c \"mklink /J \"{junction}\" \"{target}\"\""
        };
        using var command = Process.Start(start)!;
        command.WaitForExit();
        Require(command.ExitCode == 0, "Could not create junction regression fixture.");
        try
        {
            var junctionInfo = new DirectoryInfo(junction);
            Require((junctionInfo.Attributes & FileAttributes.ReparsePoint) != 0,
                "Junction fixture is not a reparse point.");
            Require(!StartupRegistration.IsProtectedEntry(junctionInfo), "Junction accepted.");
        }
        finally { Directory.Delete(junction); }
    }
    finally { Directory.Delete(root, true); }
}
CheckStartupSecurity();
string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
Require(StartupRegistration.IsProtectedInstallPath(Path.Combine(programFiles, "Limiter", "Limiter.exe")),
    "Program Files installation path was rejected.");
Require(!StartupRegistration.IsProtectedInstallPath(Path.Combine(Path.GetTempPath(), "Limiter.exe")),
    "Portable start-up path was accepted.");
Require(!StartupRegistration.IsProtectedInstallPath(Path.Combine(programFiles, "..", "Users", "Public", "Limiter.exe")),
    "A path traversal escaped the Program Files check.");
Require(Localization.ResolveLanguage("system", "tr-TR") == "tr", "Turkish system language ignored.");
Require(Localization.ResolveLanguage("system", "en-GB") == "en", "English system language ignored.");
Require(Localization.ResolveLanguage("system", "de-DE") == "en", "Unsupported system language did not fall back to English.");
Require(Localization.ResolveLanguage("en", "tr-TR") == "en" && Localization.ResolveLanguage("tr", "en-US") == "tr", "Explicit language preference ignored.");
string settingsDirectory = Path.Combine(Path.GetTempPath(), "Limiter-settings-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(settingsDirectory);
try
{
    var settingsStore = new SettingsStore(Path.Combine(settingsDirectory, "settings.json"));
    Require(settingsStore.Load().Language == "system", "First launch must follow system language.");
    settingsStore.Save(new UserSettings { Language = "en" });
    Require(settingsStore.Load().Language == "en", "Language preference did not survive reload.");
    settingsStore.Save(new UserSettings { Language = "tr" });
    Require(settingsStore.Load().Language == "tr", "Turkish preference did not survive reload.");
    settingsStore.Save(new UserSettings { Language = "unsupported" });
    Require(settingsStore.Load().Language == "system", "Invalid saved language did not fall back to system.");
    Localization.Current.SetLanguage("en");
    Require(Localization.T("download") == "Download", "English resource missing.");
    Localization.Current.SetLanguage("tr");
    Require(Localization.T("download") == "İndirme", "Turkish resource missing.");
    string occupied = Path.Combine(settingsDirectory, "occupied");
    File.WriteAllText(occupied, "occupied");
    bool rejected = false;
    try { new SettingsStore(Path.Combine(occupied, "settings.json")).Save(new UserSettings { Language = "en" }); }
    catch (IOException) { rejected = true; }
    Require(rejected && File.ReadAllText(occupied) == "occupied", "Failed settings save was not rejected safely.");
}
finally { Directory.Delete(settingsDirectory, true); }
Console.WriteLine("Language defaults, system fallback, explicit overrides, settings reload and rejected save: OK");
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
// Exercise a queued packet without opening capture handles or changing saved rules.
using (var toggleEngine = new TrafficEngine())
{
    const System.Reflection.BindingFlags hidden = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
    var engineType = typeof(TrafficEngine);
    var store = (RuleStore)engineType.GetField("_rules", hidden)!.GetValue(toggleEngine)!;
    var rules = (Dictionary<string, AppRule>)typeof(RuleStore).GetField("_rules", hidden)!.GetValue(store)!;
    const string testPath = "limiter-toggle-check.exe";
    rules[testPath] = new AppRule { Path = testPath, DownloadKBps = 16, UploadKBps = 32 };
    var getLimit = engineType.GetMethod("GetAppLimit", hidden)!;
    int Effective(bool outbound) => (int)getLimit.Invoke(toggleEngine, [testPath, outbound])!;
    Require(Effective(false) == 16 && Effective(true) == 32, "Initial limits missing.");
    var packetType = engineType.GetNestedType("Packet", System.Reflection.BindingFlags.NonPublic)!;
    long future = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
    var packet = Activator.CreateInstance(packetType, [new byte[1024], new byte[80], future, testPath, 101, false, null])!;
    var pending = engineType.GetField("_pending", hidden)!;
    var queueObject = pending.GetValue(toggleEngine)!;
    queueObject.GetType().GetMethod("Enqueue")!.Invoke(queueObject, [packet, future]);
    engineType.GetField("_pendingBytes", hidden)!.SetValue(toggleEngine, 1024L);
    toggleEngine.SetLimiterEnabled(false);
    Require(!toggleEngine.LimiterEnabled && Effective(false) == 0 && Effective(true) == 0, "Disabled limiter still applies caps.");
    var releasedQueue = pending.GetValue(toggleEngine)!;
    var releasedPacket = releasedQueue.GetType().GetMethod("Peek")!.Invoke(releasedQueue, null)!;
    long releasedDue = (long)packetType.GetProperty("Due")!.GetValue(releasedPacket)!;
    Require(releasedDue <= Stopwatch.GetTimestamp(), "Disabling did not release the delayed packet.");
    Require(store.Get(testPath).DownloadKBps == 16 && store.Get(testPath).UploadKBps == 32, "Toggle changed saved rules.");
    toggleEngine.SetLimiterEnabled(true);
    Require(toggleEngine.LimiterEnabled && Effective(false) == 16 && Effective(true) == 32, "Re-enabling did not restore caps.");
    var resumedQueue = pending.GetValue(toggleEngine)!;
    var resumedPacket = resumedQueue.GetType().GetMethod("Peek")!.Invoke(resumedQueue, null)!;
    Require((long)packetType.GetProperty("Due")!.GetValue(resumedPacket)! > releasedDue, "Re-enabling did not pace the queued packet.");
}
Console.WriteLine("Global limiter toggle, queued packet release and rule preservation: OK");
string ruleTestDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Limiter-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(ruleTestDirectory);
try
{
    string ruleFile = System.IO.Path.Combine(ruleTestDirectory, "rules.json");
    File.WriteAllText(ruleFile, "[{\"Path\":\"legacy.exe\",\"DownloadKBps\":64,\"UploadKBps\":32}]");
    var ruleStore = new RuleStore(ruleFile);
    Require(ruleStore.GetLimit("legacy.exe", false) == 64 && ruleStore.GetLimit("legacy.exe", true) == 32,
        "Legacy rules lost enabled defaults.");
    string malformedFile = System.IO.Path.Combine(ruleTestDirectory, "malformed.json");
    File.WriteAllText(malformedFile, "[{\"Path\":\"kept.exe\",\"DownloadKBps\":64}");
    var malformedStore = new RuleStore(malformedFile);
    bool rejectedSave = false;
    try { malformedStore.Set(new AppRule { Path = "new.exe", DownloadKBps = 16 }); }
    catch (InvalidOperationException) { rejectedSave = true; }
    Require(malformedStore.LoadError is not null && rejectedSave &&
        File.ReadAllText(malformedFile) == "[{\"Path\":\"kept.exe\",\"DownloadKBps\":64}",
        "Malformed rules were not preserved or saving was allowed.");
    string partialFile = System.IO.Path.Combine(ruleTestDirectory, "missing-path.json");
    File.WriteAllText(partialFile, "[{\"Path\":\"valid.exe\",\"DownloadKBps\":32},{\"DownloadKBps\":64}]");
    var partialStore = new RuleStore(partialFile);
    Require(partialStore.LoadError is not null && partialStore.GetLimit("valid.exe", false) == 0,
        "A partially valid rule file was applied.");
    using var directionEngine = new TrafficEngine(ruleStore);
    var ownPath = Environment.ProcessPath!;
    int ownPid = Environment.ProcessId;
    var directionRule = new AppRule { Path = ownPath, DownloadKBps = 64, UploadKBps = 32, DownloadLimitEnabled = false, BlockDownload = true };
    directionEngine.SetRule(directionRule);
    Require(ruleStore.GetLimit(ownPath, false) == 0 && ruleStore.GetLimit(ownPath, true) == 32, "Directional limit disable affected the other direction.");
    var reloaded = new RuleStore(ruleFile);
    Require(reloaded.Get(ownPath).DownloadKBps == 64 && !reloaded.Get(ownPath).DownloadLimitEnabled && reloaded.Get(ownPath).BlockDownload,
        "Directional settings or saved value did not survive reload.");
    const System.Reflection.BindingFlags hidden = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
    var engineType = typeof(TrafficEngine);
    var blockMethod = engineType.GetMethod("IsBlocked", hidden)!;
    bool Blocked(int pid, bool outbound) => (bool)blockMethod.Invoke(directionEngine, [ownPath, pid, outbound])!;
    Require(Blocked(ownPid, false) && !Blocked(ownPid, true), "Download blocker affected upload.");
    directionEngine.SetLimiterEnabled(false);
    Require(Blocked(ownPid, false), "Limiter Off disabled the independent blocker.");
    directionEngine.SetBlockerEnabled(false);
    Require(!Blocked(ownPid, false), "Global blocker toggle did not bypass the rule.");
    directionEngine.SetBlockerEnabled(true);
    directionEngine.SetRule(new AppRule { Path = ownPath });
    directionEngine.SetRule(new AppRule { Path = ownPath, BlockUpload = true }, ownPid);
    Require(Blocked(ownPid, true) && !Blocked(ownPid + 100000, true) && !Blocked(ownPid, false), "PID blocker leaked to siblings or download.");
    var packetType = engineType.GetNestedType("Packet", System.Reflection.BindingFlags.NonPublic)!;
    long future = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
    var packet = Activator.CreateInstance(packetType, [new byte[1024], new byte[80], future, ownPath, ownPid, true, null])!;
    var pendingField = engineType.GetField("_pending", hidden)!;
    var pending = pendingField.GetValue(directionEngine)!;
    pending.GetType().GetMethod("Enqueue")!.Invoke(pending, [packet, future]);
    engineType.GetField("_pendingBytes", hidden)!.SetValue(directionEngine, 1024L);
    directionEngine.SetRule(new AppRule { Path = ownPath, BlockUpload = true }, ownPid);
    Require((int)pendingField.GetValue(directionEngine)!.GetType().GetProperty("Count")!.GetValue(pendingField.GetValue(directionEngine))! == 0,
        "Blocked packet remained queued.");
    Require((long)engineType.GetField("_pendingBytes", hidden)!.GetValue(directionEngine)! == 0, "Blocked packet left queue bytes behind.");
    directionEngine.SetRule(new AppRule { Path = ownPath }, ownPid);
    Require(!Blocked(ownPid, true), "Clearing PID blocker did not restore traffic.");
    directionEngine.SetRule(new AppRule { Path = ownPath, DownloadKBps = 64, DownloadLimitEnabled = true });
    Require(ruleStore.GetLimit(ownPath, false) == 64, "Re-enabling did not restore the saved limit.");
    // Force a persistence failure without touching the real user's rules.
    string badParent = System.IO.Path.Combine(ruleTestDirectory, "not-a-directory");
    File.WriteAllText(badParent, "occupied");
    var failingStore = new RuleStore(System.IO.Path.Combine(badParent, "rules.json"));
    using var failingEngine = new TrafficEngine(failingStore);
    bool failed = false;
    try { failingEngine.SetRule(new AppRule { Path = ownPath, BlockDownload = true }); }
    catch (IOException) { failed = true; }
    Require(failed && !failingStore.Get(ownPath).BlockDownload, "Failed persistence changed runtime rules.");
    Console.WriteLine("Legacy compatibility, directional persistence, independent toggles, PID blocker isolation, queue removal and rejected save: OK");
}
finally { Directory.Delete(ruleTestDirectory, true); }
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
    var processSeen = (Dictionary<string, Dictionary<int, long>>)typeof(TrafficEngine)
        .GetField("_processSeen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .GetValue(engine)!;
    processSeen[owner.Path][owner.ProcessId] = Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * 600);
    var pruned = engine.Snapshot().Single(app => app.Path == owner.Path);
    Require(pruned.DownloadBytes == 1500 && pruned.Processes.Count == 0,
        "Inactive PID history was not pruned or the application total was lost.");
    engine.RecordSent(owner, 1, false);
    Require(engine.Snapshot().Single(app => app.Path == owner.Path).Processes
        .Any(process => process.ProcessId == owner.ProcessId), "Pruned PID row did not return with new traffic.");
    Console.WriteLine("Application and PID traffic totals: OK");
    int originalAppDown = usage.DownloadLimit;
    engine.SetProcessLimits(owner.Path, owner.ProcessId, 64, 0);
    processSeen[owner.Path][owner.ProcessId] = Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * 600);
    var limited = engine.Snapshot().Single(app => app.Path == owner.Path);
    Require(limited.DownloadLimit == originalAppDown &&
        limited.Processes.Single(process => process.ProcessId == owner.ProcessId).DownloadLimit == 64,
        "PID rule changed the application rule.");
    // Removal is independent of inactivity pruning (already checked above).
    engine.RecordSent(owner, 1, false);
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
