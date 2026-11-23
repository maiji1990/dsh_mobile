using System.Net;
using System.Net.Sockets;
using System.Text;
using DshMobile.Runtime.Supervision;

namespace DshMobile.Runtime.Tests;

public sealed class SupervisionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-proc-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---------------------------------------------------------------- health policy (shared vectors)

    private static DshWindowState State(System.Text.Json.JsonElement a) => new(
        a[0].GetBoolean(), a[1].GetInt32(), a[2].ValueKind == System.Text.Json.JsonValueKind.Null ? null : a[2].GetInt64(), a[3].GetInt64(), a[4].GetInt32());

    [Fact]
    public void HealthPolicy_MatchesSharedVectors()
    {
        var doc = Repo.Vectors("dsh-health-policy.json");
        var p = doc.GetProperty("params");
        var cases = doc.GetProperty("cases").EnumerateArray().ToList();
        Assert.True(cases.Count >= 12);
        foreach (var c in cases)
        {
            var pr = c.GetProperty("probe");
            var d = DshHealthPolicy.Decide(State(c.GetProperty("state")),
                new DshHealthProbe(pr[0].GetInt64(), pr[1].GetBoolean(), pr[2].GetBoolean(), pr[3].GetBoolean(), pr[4].GetInt64()),
                p.GetProperty("readyTimeoutMs").GetInt64(), p.GetProperty("unresponsiveGraceMs").GetInt64(), p.GetProperty("maxRestartAttempts").GetInt32());
            var label = c.GetProperty("name").GetString();
            Assert.True(c.GetProperty("decision").GetString() == d.Action.ToString(), $"{label}: {d.Action}");
            Assert.True(State(c.GetProperty("next")) == d.Next, $"{label}: {d.Next}");
        }
    }

    // ---------------------------------------------------------------- /proc snapshot (fake proc root)

    private void Proc(int pid, int ppid, string comm, params string[] argv)
    {
        var d = Path.Combine(_dir, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "stat"), $"{pid} ({comm}) S {ppid} {pid} 0 0 -1 4194560 0 0");
        File.WriteAllText(Path.Combine(d, "cmdline"), string.Join('\0', argv) + "\0");
    }

    /// <summary>The app (100) with the sandbox keepalive PRoot tree and the DSH PRoot tree, like on the device.</summary>
    private void DeviceLikeTree()
    {
        Proc(1, 0, "init", "/init");
        Proc(100, 1, "dshmobile", "org.example.dshmobile");
        Proc(200, 100, "libproot.so", "libproot.so", "--rootfs=/x/base", "/usr/bin/bash", "-c", "echo dshapp-sandbox-keepalive; while true; do sleep 3600; done");
        Proc(201, 200, "bash", "/usr/bin/bash", "-c", "echo dshapp-sandbox-keepalive");
        Proc(202, 201, "sleep", "sleep", "3600");
        Proc(300, 100, "libproot.so", "libproot.so", "--rootfs=/x/base", "/usr/local/bin/node", "/opt/dshapp/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js", "--profile", "web");
        Proc(301, 300, "node", "node", "--expose-internals", "/opt/dshapp/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js");
        Proc(400, 1, "other", "unrelated", "@deepseek-ai/dsh/lib/bin.js"); // not under the app: never matched
        Directory.CreateDirectory(Path.Combine(_dir, "self"));
        Directory.CreateDirectory(Path.Combine(_dir, "999")); // vanished process: no stat, skipped
    }

    [Fact]
    public void Observe_FindsBothTreesUnderTheApp()
    {
        DeviceLikeTree();
        var s = RuntimeProcessSnapshot.Observe(100, _dir)!;
        Assert.Equal(200, s.SandboxRootPid);
        Assert.Equal(3, s.SandboxTreeSize);
        Assert.Equal(300, s.DshRootPid);
        Assert.Equal(2, s.DshTreeSize);
    }

    [Fact]
    public void Observe_NothingRunning_IsEmptyNotNull()
    {
        Proc(100, 1, "dshmobile", "org.example.dshmobile");
        var s = RuntimeProcessSnapshot.Observe(100, _dir)!;
        Assert.Null(s.SandboxRootPid);
        Assert.Null(s.DshRootPid);
        Assert.Null(RuntimeProcessSnapshot.Observe(100, Path.Combine(_dir, "missing")));
    }

    [Fact]
    public void Table_UsesBfsWithSortedChildren()
    {
        DeviceLikeTree();
        var t = ProcessTable.Read(_dir)!;
        Assert.Equal([200, 300, 201, 301, 202], t.DescendantPids(100));
        Assert.Equal([100, 400], t.ChildrenOf(1));
        Assert.Equal("libproot.so --rootfs=/x/base /usr/local/bin/node /opt/dshapp/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js --profile web ", t.Cmdline(300));
    }

    /// <summary>
    /// Legacy reverses a post-order (children-first) list, so it actually kills PARENTS first, contrary to its own
    /// comment. Traced by hand from killAll(): allDesc = [201, 202, 200], post-order = [202, 201, 200], reversed.
    /// </summary>
    [Fact]
    public void KillAllOrder_MatchesLegacyParentsFirstOrder()
    {
        DeviceLikeTree();
        var order = ProcessTable.Read(_dir)!.KillAllOrder(RuntimeConstants.SandboxKeepaliveMarker);
        Assert.Equal([200, 201, 202], order);
        Assert.Empty(ProcessTable.Read(_dir)!.KillAllOrder("no-such-marker"));
    }

    [Fact]
    public void Stat_ParsingUsesLastParenRule_HandlesParensInComm()
    {
        // N-08-01: comm containing ") " or ")" must still parse state, ppid, and startTicks using LastIndexOf(')').
        ProcWithMeta(10, 1, "a) b (worker)", 'S', 98765, uid: 10123, tracerPid: 0, argv: ["x"]);
        Proc(11, 1, "fine", "y");
        var t = ProcessTable.Read(_dir)!;
        Assert.Equal(1, t.ParentOf[10]);
        Assert.Equal(1, t.ParentOf[11]);
        var d = t.GetDetails(10)!;
        Assert.Equal('S', d.State);
        Assert.Equal(98765, d.StartTicks);
        Assert.Equal(10123, d.Uid);
        Assert.Equal(0, d.TracerPid);
    }

    private void ProcWithMeta(
        int pid,
        int ppid,
        string comm,
        char state,
        long startTicks,
        int? uid,
        int? tracerPid,
        params string[] argv)
    {
        var d = Path.Combine(_dir, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(d);
        // /proc/<pid>/stat: fields 1..2 are "pid (comm)", followed by 19 fields before field 22 (startTicks = index 19 after ')').
        var padding = string.Join(' ', Enumerable.Repeat("0", 17));
        File.WriteAllText(Path.Combine(d, "stat"), $"{pid} ({comm}) {state} {ppid} {padding} {startTicks} 0 0");
        if (uid is not null || tracerPid is not null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Name:\t{comm}");
            if (tracerPid is not null)
            {
                sb.AppendLine($"TracerPid:\t{tracerPid.Value}");
            }
            if (uid is not null)
            {
                sb.AppendLine($"Uid:\t{uid.Value}\t{uid.Value}\t{uid.Value}\t{uid.Value}");
            }
            File.WriteAllText(Path.Combine(d, "status"), sb.ToString());
        }
        File.WriteAllText(Path.Combine(d, "cmdline"), string.Join('\0', argv) + "\0");
    }

    [Fact]
    public void NativeOrphanReaper_FindsAndKillsOnlyConservativeIntersection()
    {
        const int appUid = 10234;
        const int appPid = 100;
        const int liveSandbox = 200;
        var exeTargets = new Dictionary<int, string?>
        {
            [100] = "/system/bin/app_process64",
            [200] = "/data/app/lib/arm64/libproot.so",
            [201] = "/data/app/lib/arm64/libproot-loader.so",      // alive under sandbox tree -> keep
            [500] = "/data/app/lib/arm64/libproot-loader.so",      // orphan match -> kill
            [501] = "/data/app/lib/arm64/libproot-loader32.so (deleted)", // deleted loader orphan -> kill
            [502] = "/data/app/lib/arm64/libproot-loader.so",      // wrong UID -> keep
            [503] = "/data/app/lib/arm64/libproot-loader.so",      // ppid != 1 -> keep
            [504] = "/data/app/lib/arm64/libproot-loader.so",      // traced (TracerPid=999) -> keep
            [505] = "/data/app/lib/arm64/libproot-loader.so",      // zombie ('Z') -> keep
            [506] = "/usr/bin/node",                               // non-loader exe -> keep
            [507] = null,                                          // unreadable exe -> skip
            [508] = "/data/app/lib/arm64/libproot-loader.so",      // missing status (uid/tracer) -> skip
        };

        ProcWithMeta(1, 0, "init", 'S', 1, uid: 0, tracerPid: 0, "/init");
        ProcWithMeta(appPid, 1, "dshmobile", 'S', 1000, uid: appUid, tracerPid: 0, "org.example.dshmobile");
        ProcWithMeta(liveSandbox, appPid, "libproot.so", 'S', 2000, uid: appUid, tracerPid: 0, "libproot.so");
        ProcWithMeta(201, liveSandbox, "loader", 'S', 2001, uid: appUid, tracerPid: 0, "libproot-loader.so");
        ProcWithMeta(500, 1, "libproot-loader", 'S', 3000, uid: appUid, tracerPid: 0, "libproot-loader.so");
        ProcWithMeta(501, 1, "loader32", 'R', 3001, uid: appUid, tracerPid: 0, "libproot-loader32.so");
        ProcWithMeta(502, 1, "other-uid", 'S', 3002, uid: 10999, tracerPid: 0, "libproot-loader.so");
        ProcWithMeta(503, 999, "adopted", 'S', 3003, uid: appUid, tracerPid: 0, "libproot-loader.so");
        ProcWithMeta(504, 1, "traced", 't', 3004, uid: appUid, tracerPid: 999, "libproot-loader.so");
        ProcWithMeta(505, 1, "zombie", 'Z', 3005, uid: appUid, tracerPid: 0, "libproot-loader.so");
        ProcWithMeta(506, 1, "node", 'S', 3006, uid: appUid, tracerPid: 0, "node");
        ProcWithMeta(507, 1, "noexe", 'S', 3007, uid: appUid, tracerPid: 0, "unknown");
        ProcWithMeta(508, 1, "nostatus", 'S', 3008, uid: null, tracerPid: null, "libproot-loader.so");

        var killed = new List<int>();
        var report = NativeOrphanReaper.Reap(
            _dir,
            appPid: appPid,
            liveRootPids: [liveSandbox],
            killPid: pid => { killed.Add(pid); return true; },
            readExeTarget: pid => exeTargets.GetValueOrDefault(pid));

        Assert.Equal(2, report.CandidatesFound);
        Assert.Equal([500, 501], report.KilledPids);
        Assert.Empty(report.SkippedPids);
        Assert.Equal([500, 501], killed);
    }

    [Fact]
    public void NativeOrphanReaper_SkipsCandidate_WhenStartTicksChangedBeforeKill()
    {
        const int appUid = 10234;
        const int appPid = 100;
        ProcWithMeta(appPid, 1, "dshmobile", 'S', 1000, uid: appUid, tracerPid: 0, "org.example.dshmobile");
        ProcWithMeta(600, 1, "loader", 'S', 4444, uid: appUid, tracerPid: 0, "libproot-loader.so");

        var table = ProcessTable.Read(_dir, pid => pid == 600 ? "/data/lib/libproot-loader.so" : null)!;
        var found = NativeOrphanReaper.FindOrphans(table, appUid, appPid, []);
        Assert.Single(found);

        // Simulate PID reuse right before kill by overwriting stat with a new startTicks.
        var statPath = Path.Combine(_dir, "600", "stat");
        var padding = string.Join(' ', Enumerable.Repeat("0", 17));
        File.WriteAllText(statPath, $"600 (newproc) S 1 {padding} 9999 0 0");

        var killed = new List<int>();
        // Directly re-verify: if startTicks changed between FindOrphans and live check, TryReadLiveStat catches it.
        Assert.True(table.TryReadLiveStat(600, out _, out _, out var liveTicks));
        Assert.NotEqual(found[0].StartTicks, liveTicks);
        Assert.Empty(killed);
    }

    [Fact]
    public void NativeOrphanReaper_FailsClosed_WhenSelfUidUnknown()
    {
        ProcWithMeta(100, 1, "dshmobile", 'S', 1000, uid: null, tracerPid: null, "org.example.dshmobile");
        ProcWithMeta(500, 1, "loader", 'S', 3000, uid: 0, tracerPid: 0, "libproot-loader.so");
        var killed = new List<int>();
        var report = NativeOrphanReaper.Reap(_dir, appPid: 100, killPid: pid => { killed.Add(pid); return true; }, readExeTarget: _ => "/libproot-loader.so");
        Assert.Equal(0, report.CandidatesFound);
        Assert.Empty(killed);
    }

    // ---------------------------------------------------------------- HTTP probe (local listeners)

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task ServeOnceAsync(TcpListener listener, string? response)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var buf = new byte[4096];
        _ = await stream.ReadAsync(buf);
        if (response is not null)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
        }
    }

    [Theory]
    [InlineData(401)]
    [InlineData(200)]
    [InlineData(302)]
    [InlineData(503)]
    public async Task AnyHttpStatus_IsReady(int status)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // One connection: the TCP check and the HTTP request share it.
        var server = Task.Run(() => ServeOnceAsync(listener, $"HTTP/1.1 {status} X\r\nContent-Length: 0\r\nLocation: /x\r\nConnection: close\r\n\r\n"));
        var h = await HttpHealthProbe.CheckAsync("127.0.0.1", port);
        listener.Stop();
        Assert.True(h.PortOpen);
        Assert.True(h.WebUiReady, h.Error);
        Assert.Equal(status, h.HttpStatus);
        await server;
    }

    [Theory]
    [InlineData("HTTP/1.1 401 Unauthorized", 401)]
    [InlineData("HTTP/1.0 200 OK", 200)]
    [InlineData("HTTP/1.1 999 Odd", 999)]
    [InlineData("SSH-2.0-OpenSSH_9.9", null)]
    [InlineData("HTTP/1.1 20 Short", null)]
    [InlineData(null, null)]
    public void StatusLine_Parsing(string? line, int? code) => Assert.Equal(code, HttpHealthProbe.ParseStatusCode(line));

    [Fact]
    public async Task NonHttpBanner_IsOpenButNotReady()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(() => ServeOnceAsync(listener, "SSH-2.0-OpenSSH_9.9\r\n"));
        var h = await HttpHealthProbe.CheckAsync("127.0.0.1", port);
        listener.Stop();
        Assert.True(h.PortOpen);
        Assert.False(h.WebUiReady);
        await server;
    }

    [Fact]
    public async Task ClosedPort_IsNotOpen()
    {
        var h = await HttpHealthProbe.CheckAsync("127.0.0.1", FreePort(), connectTimeout: TimeSpan.FromSeconds(1));
        Assert.False(h.PortOpen);
        Assert.False(h.WebUiReady);
    }

    [Fact]
    public async Task OpenPortWithoutHttp_IsOpenButNotReady()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(() => ServeOnceAsync(listener, null)); // read the request, answer nothing, close
        var h = await HttpHealthProbe.CheckAsync("127.0.0.1", port, readTimeout: TimeSpan.FromSeconds(1));
        listener.Stop();
        Assert.True(h.PortOpen);
        Assert.False(h.WebUiReady);
        await server;
    }
}
