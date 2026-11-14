using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DshMobile.Runtime.Remote;

namespace DshMobile.Runtime.Tests;

/// <summary>Runs only where Windows OpenSSH (sshd.exe + ssh-keygen.exe) is installed; reported as skipped elsewhere.</summary>
public sealed class WindowsOpenSshFactAttribute : FactAttribute
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH");

    public WindowsOpenSshFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Path.Combine(Dir, "sshd.exe")) || !File.Exists(Path.Combine(Dir, "ssh-keygen.exe")))
        {
            Skip = "Windows OpenSSH server is not installed";
        }
    }
}

/// <summary>Also needs the upstream <c>dsh</c> on this machine's PATH (the remote of the start / stop test).</summary>
public sealed class WindowsOpenSshWithDshFactAttribute : FactAttribute
{
    public WindowsOpenSshWithDshFactAttribute()
    {
        if (new WindowsOpenSshFactAttribute().Skip is { } skip)
        {
            Skip = skip;
            return;
        }
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!path.Split(Path.PathSeparator).Any(d => d.Length > 0 && (File.Exists(Path.Combine(d, "dsh.cmd")) || File.Exists(Path.Combine(d, "dsh.exe")))))
        {
            Skip = "dsh is not on PATH";
        }
    }
}

/// <summary>
/// PR-09 second segment (ADR-0012) end to end against a real OpenSSH server: this machine's sshd.exe started as an
/// ordinary user process in debug mode (one connection, loopback, a throwaway host key and authorized_keys) — no system
/// service is touched. The client key is a software ECDsa behind <see cref="ISshKeySource"/>, which returns DER exactly
/// like Android Keystore.
/// </summary>
public sealed class RemoteSshOpenSshTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-sshd-").FullName;
    private readonly ECDsa _client = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly List<Process> _servers = [];

    [WindowsOpenSshFact]
    public async Task PinnedOnlyPolicy_AndProbe_AgainstWindowsOpenSsh()
    {
        // Host keys of all three kinds, ecdsa listed first: the client order (JSch's) must still pick ed25519.
        foreach (var t in new[] { "ecdsa", "rsa", "ed25519" })
        {
            Run("ssh-keygen.exe", "-q", "-t", t, "-N", "", "-f", Path.Combine(_dir, "host_" + t));
        }
        var edLine = File.ReadAllText(Path.Combine(_dir, "host_ed25519.pub")).Split(' ');
        var hostFp = SshWire.Fingerprint(Convert.FromBase64String(edLine[1]));
        var keys = new Keys(_client);
        File.WriteAllText(Path.Combine(_dir, "authorized_keys"), SshWire.OpenSshLine(SshIdentity.PublicBlob(keys.ReadPublicKey()), "test") + "\n");
        var user = Environment.UserName;

        // 1. No pin: refused during key exchange (before any signature), fingerprint + type reported, no session.
        var a = await Attempt(keys, user, null);
        Assert.Equal(("DISCONNECTED", "HOST_KEY_UNKNOWN", RemoteConnectRules.UnknownMessage), (a.State, a.Code, a.Message));
        Assert.Equal((hostFp, "ssh-ed25519"), (a.Fingerprint, a.KeyType));
        Assert.Null(a.Session);
        Assert.Equal(0, keys.Signs);

        // 2. A different pin: CHANGED, fingerprint only.
        a = await Attempt(new Keys(_client), user, "SHA256:" + new string('A', 43));
        Assert.Equal(("DISCONNECTED", "HOST_KEY_CHANGED", RemoteConnectRules.ChangedMessage), (a.State, a.Code, a.Message));
        Assert.Equal((hostFp, (string?)null), (a.Fingerprint, a.KeyType));

        // 3. Pinned, but a key the server does not know: AUTH_FAILED.
        a = await Attempt(new Keys(_stranger), user, hostFp);
        Assert.Equal(("DISCONNECTED", "AUTH_FAILED", RemoteConnectRules.AuthMessage), (a.State, a.Code, a.Message));
        Assert.Null(a.Fingerprint);
        Assert.Equal(hostFp, a.SeenFingerprint);

        // 4. Pinned + authorized: connected, one Keystore-style signature, the fixed probe ran.
        var signer = new Keys(_client);
        a = await Attempt(signer, user, hostFp);
        try
        {
            Assert.Equal("CONNECTED", a.State);
            Assert.Equal(1, signer.Signs);
            Assert.NotNull(a.Probe);
            Assert.Equal(a.Probe!.Dsh ? "OK" : "DSH_MISSING", a.Code);
            Assert.False(a.ProbeTimedOut);
            Assert.NotNull(a.Session);
            Assert.True(a.Session!.IsConnected);
            var exit = await a.Session.ExecAsync("cmd /c exit 3", TimeSpan.FromSeconds(20), CancellationToken.None);
            Assert.Equal(3, exit.ExitStatus);
        }
        finally
        {
            a.Session?.Dispose();
        }
        Assert.False(a.Session!.IsConnected);
    }

    /// <summary>
    /// PR-09 third segment (ADR-0013): the real upstream dsh web as the remote — launch line, token in memory only, local
    /// forward answering 401 without the token, the shadow's own-forward check, stop verdict "not running".
    /// </summary>
    [WindowsOpenSshWithDshFact]
    public async Task DshWebStartStop_AgainstWindowsOpenSsh()
    {
        Run("ssh-keygen.exe", "-q", "-t", "ed25519", "-N", "", "-f", Path.Combine(_dir, "host_ed25519"));
        File.Copy(Path.Combine(_dir, "host_ed25519"), Path.Combine(_dir, "host_ecdsa"));
        File.Copy(Path.Combine(_dir, "host_ed25519"), Path.Combine(_dir, "host_rsa"));
        var hostFp = SshWire.Fingerprint(Convert.FromBase64String(File.ReadAllText(Path.Combine(_dir, "host_ed25519.pub")).Split(' ')[1]));
        var keys = new Keys(_client);
        File.WriteAllText(Path.Combine(_dir, "authorized_keys"), SshWire.OpenSshLine(SshIdentity.PublicBlob(keys.ReadPublicKey()), "test") + "\n");
        var dshPort = FreePort();
        var a = await Attempt(keys, Environment.UserName, hostFp, dshPort, probe: false);
        using var session = a.Session ?? throw new InvalidOperationException($"{a.Code}: {a.Message}");

        var nothing = await session.ProbeHttpAsync(dshPort);
        Assert.Null(nothing); // nothing listens before the start

        var started = await session.StartDshAsync();
        Assert.True(started is { State: "READY", Code: "OK", HttpUnauthenticated: 401, HasLaunchToken: true }, started.ToString());
        Assert.NotNull(started.LocalPort);
        Assert.Equal(started.LocalPort, session.LocalPort);
        Assert.Equal("READY", session.Refresh().State);
        Assert.Equal(401, await session.ProbeHttpAsync(dshPort)); // the shadow's own-forward check sees the same

        var stopped = await session.StopDshAsync();
        Assert.True(stopped is { State: "CONNECTED", Code: "OK", RemoteDshStillRunning: false, HasLaunchToken: false, LocalPort: null }, stopped.ToString());
        Assert.Null(await session.ProbeHttpAsync(dshPort));
        Assert.Null(await RemoteSshSession.HttpStatusAsync(started.LocalPort!.Value, CancellationToken.None)); // forward closed
    }

    /// <summary>One sshd -d per attempt (debug mode serves a single connection, then exits).</summary>
    private async Task<RemoteConnectAttempt> Attempt(Keys keys, string user, string? pinned, int dshPort = 3091, bool probe = true)
    {
        var port = FreePort();
        var config = Path.Combine(_dir, $"sshd_config_{port}");
        File.WriteAllLines(config,
        [
            $"Port {port}", "ListenAddress 127.0.0.1",
            $"HostKey \"{Fwd("host_ecdsa")}\"", $"HostKey \"{Fwd("host_rsa")}\"", $"HostKey \"{Fwd("host_ed25519")}\"",
            $"AuthorizedKeysFile \"{Fwd("authorized_keys")}\"", "PasswordAuthentication no", "PubkeyAuthentication yes",
            "StrictModes no", $"PidFile \"{Fwd($"sshd_{port}.pid")}\"",
        ]);
        var sshd = Process.Start(new ProcessStartInfo(Path.Combine(WindowsOpenSshFactAttribute.Dir, "sshd.exe"))
        {
            ArgumentList = { "-D", "-d", "-e", "-f", config },
            RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false,
        })!;
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        sshd.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }
            log.Enqueue(e.Data);
            if (e.Data.Contains("Server listening", StringComparison.Ordinal))
            {
                listening.TrySetResult();
            }
        };
        _servers.Add(sshd);
        sshd.BeginErrorReadLine();
        sshd.BeginOutputReadLine();
        try
        {
            await listening.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var profile = new RemoteHostProfile("id-1", "pc", "127.0.0.1", port, user, dshPort, pinned);
            var connector = new RemoteSshConnector(keys);
            return probe ? await connector.ConnectAsync(profile) : await connector.ConnectSessionAsync(profile);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("sshd did not start: " + string.Join(" | ", log));
        }
    }

    private string Fwd(string name) => Path.Combine(_dir, name).Replace('\\', '/');

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private void Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(WindowsOpenSshFactAttribute.Dir, exe)) { UseShellExecute = false, RedirectStandardInput = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        Assert.True(p.WaitForExit(30_000), exe);
        Assert.Equal(0, p.ExitCode);
    }

    public void Dispose()
    {
        foreach (var p in _servers)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(true);
                    p.WaitForExit(5_000);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            p.Dispose();
        }
        _client.Dispose();
        _stranger.Dispose();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // sshd may still hold a file for a moment; the temp folder is cleaned by the OS later.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class Keys(ECDsa key) : ISshKeySource
    {
        public int Signs;

        public SshPublicKey ReadPublicKey()
        {
            var p = key.ExportParameters(false);
            return new SshPublicKey(p.Q.X!, p.Q.Y!, false);
        }

        SshPublicKey? ISshKeySource.ReadPublicKey() => ReadPublicKey();

        public byte[] SignDer(byte[] data)
        {
            Interlocked.Increment(ref Signs);
            return key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
    }
}
