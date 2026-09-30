using System.Net.Sockets;
using System.Reflection;
using Limiter;

internal static class LiveBlockerChecks
{
    internal static async Task Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Limiter-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var engine = new TrafficEngine(new RuleStore(Path.Combine(directory, "rules.json")));
            engine.Start();
            using var udp = new UdpClient();
            udp.Connect("1.1.1.1", 53);
            // DNS A query for example.com; the test never edits the user's rules file.
            byte[] query = [0x12, 0x34, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0,
                7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
                3, (byte)'c', (byte)'o', (byte)'m', 0, 0, 1, 0, 1];
            var counter = typeof(TrafficEngine).GetField("_blockedPackets", BindingFlags.Instance | BindingFlags.NonPublic)!;
            long BlockedPackets() => (long)counter.GetValue(engine)!;
            async Task<bool> Query()
            {
                await udp.SendAsync(query);
                using var timeout = new CancellationTokenSource(2500);
                try
                {
                    var answer = await udp.ReceiveAsync(timeout.Token);
                    return answer.Buffer.Length > 12;
                }
                catch (OperationCanceledException) { return false; }
            }
            async Task ExpectBlocked(string direction)
            {
                long before = BlockedPackets();
                if (await Query()) throw new Exception(direction + " blocker allowed the DNS exchange.");
                if (BlockedPackets() <= before) throw new Exception(direction + " did not count a native blocked packet.");
            }
            if (!await Query()) throw new Exception("Baseline DNS reply unavailable.");
            await Task.Delay(600);
            string path = Environment.ProcessPath!;
            engine.SetRule(new AppRule { Path = path, BlockDownload = true });
            await ExpectBlocked("Inbound");
            engine.SetBlockerEnabled(false);
            if (!await Query()) throw new Exception("Blocker Off did not restore reply.");
            engine.SetBlockerEnabled(true);
            engine.SetRule(new AppRule { Path = path, BlockUpload = true });
            await ExpectBlocked("Outbound");
            engine.SetRule(new AppRule { Path = path });
            if (!await Query()) throw new Exception("Clearing blocker did not restore reply.");
            engine.SetRule(new AppRule { Path = path, BlockUpload = true }, Environment.ProcessId);
            await ExpectBlocked("PID");
            engine.SetRule(new AppRule { Path = path }, Environment.ProcessId);
            if (!await Query()) throw new Exception("Clearing PID blocker did not restore reply.");
            Console.WriteLine("Live WinDivert UDP baseline, inbound block, outbound block, global bypass, PID block and recovery: OK");
        }
        finally { Directory.Delete(directory, true); }
    }
}
