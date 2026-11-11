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
    public void Stat_ParsingMatchesLegacyFirstParenRule()
    {
        // comm "a) b" contains ") ": legacy splits after the FIRST occurrence and reads "b" as state, "S" as ppid -> skipped.
        Proc(10, 1, "a) b", "x");
        Proc(11, 1, "fine", "y");
        var t = ProcessTable.Read(_dir)!;
        Assert.False(t.ParentOf.ContainsKey(10));
        Assert.Equal(1, t.ParentOf[11]);
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
