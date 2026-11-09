using System.Diagnostics;
using DshMobile.Core.Bridge;
using DshMobile.NativeBridge;
using Log = Android.Util.Log;

namespace DshMobile.App.Bridge;

/// <summary>
/// Application-scoped owner of the native facade and the C# client. Survives Activity
/// recreation (rotation, background/foreground) so in-flight work is not lost.
/// </summary>
public sealed class BridgeHost : IDisposable
{
    public const string LogTag = "DshMobile";
    private const int MaxLogLines = 400;

    private readonly DshNativeFacade _facade;
    private readonly NativeFacadeTransport _transport;
    private readonly object _logGate = new();
    private readonly LinkedList<string> _log = new();
    private int _busy;

    public BridgeHost(Android.Content.Context context)
    {
        _facade = new DshNativeFacade();
        _transport = new NativeFacadeTransport(_facade);
        Client = new BridgeClient(_transport);
        Client.EventReceived += (_, e) => Append($"event {e.Event} {e.Payload}");
        _facade.Initialize(context);
        Append($"facade initialized, bridge {DshNativeFacade.BridgeVersion}");
    }

    public BridgeClient Client { get; }

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>Raised on an arbitrary thread whenever the log or busy state changes.</summary>
    public event EventHandler? Changed;

    public string LogText
    {
        get
        {
            lock (_logGate)
            {
                return string.Join('\n', _log);
            }
        }
    }

    public void ClearLog()
    {
        lock (_logGate)
        {
            _log.Clear();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task RunInfoAsync() => RunExclusive("bridge.info", async () =>
    {
        var resp = await Client.ExecuteAsync(BridgeMethods.Info).ConfigureAwait(false);
        if (!resp.Ok)
        {
            Append($"bridge.info FAILED {resp.Code}: {resp.Message}");
            return;
        }
        var info = BridgeCodec.ReadPayload(resp, BridgeJsonContext.Default.BridgeInfo)!;
        Append($"bridge {info.BridgeVersion} protocol={info.Protocol} kotlin={info.KotlinVersion}");
        Append($"upstream dshbox={info.Upstream.GetValueOrDefault("dshbox", "?")}");
        Append($"device sdk={info.SdkInt} abis=[{string.Join(",", info.Abis)}] model={info.DeviceModel}");
    });

    public Task RunKotlinCompareAsync() => RunExclusive("dshbox.versions.compare", async () =>
    {
        (string A, string B)[] cases = [("0.1.1", "0.1.1-rc.2"), ("0.1.0-rc.9", "0.1.0-rc.10"), ("v1.5.1", "1.5.1")];
        foreach (var (a, b) in cases)
        {
            var payload = BridgeCodec.ToPayload(new VersionsCompareRequest(a, b), BridgeJsonContext.Default.VersionsCompareRequest);
            var resp = await Client.ExecuteAsync(BridgeMethods.DshboxVersionsCompare, payload).ConfigureAwait(false);
            Append(resp.Ok
                ? $"Versions.compare(\"{a}\", \"{b}\") = {BridgeCodec.ReadPayload(resp, BridgeJsonContext.Default.VersionsCompareResult)!.Result}"
                : $"Versions.compare FAILED {resp.Code}: {resp.Message}");
        }
    });

    /// <summary>M1 acceptance: N concurrent round-trips; afterwards both in-flight tables must be 0.</summary>
    public Task RunStressAsync(int count = 1000) => RunExclusive($"stress x{count}", async () =>
    {
        var sw = Stopwatch.StartNew();
        var gcBefore = GC.GetTotalMemory(forceFullCollection: false);
        var tasks = new Task<BridgeResponse>[count];
        for (var i = 0; i < count; i++)
        {
            tasks[i] = Client.ExecuteAsync(BridgeMethods.Ping);
        }
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        sw.Stop();

        // Native removes a request from its table right after our callback returns; allow it to settle.
        var settle = Stopwatch.StartNew();
        while (_facade.InFlightCount != 0 && settle.ElapsedMilliseconds < 1000)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        var ok = results.Count(r => r.Ok);
        var failures = results.Where(r => !r.Ok).GroupBy(r => r.Code).Select(g => $"{g.Key}={g.Count()}");
        Append($"stress: ok={ok} failed={count - ok} [{string.Join(",", failures)}] in {sw.ElapsedMilliseconds} ms");
        Append($"stress: inFlight C#={Client.InFlightCount} native={_facade.InFlightCount} orphans={Client.OrphanResponses} malformed={Client.MalformedMessages}");
        Append($"stress: managed heap {gcBefore / 1024} KiB -> {GC.GetTotalMemory(false) / 1024} KiB");
        Append($"stress: after GC {MeasureRetained()}");
    });

    /// <summary>
    /// Retained memory after forcing GC on both runtimes, plus JNI global refs. Lazy GC makes raw
    /// dumpsys numbers rise between runs; only post-GC values that keep growing indicate a leak.
    /// </summary>
    private static string MeasureRetained()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Java.Lang.JavaSystem.Gc();
        Java.Lang.JavaSystem.RunFinalization();
        Java.Lang.JavaSystem.Gc();
        var rt = Java.Lang.Runtime.GetRuntime()!;
        var javaUsed = rt.TotalMemory() - rt.FreeMemory();
        var grefs = Java.Interop.JniRuntime.CurrentRuntime.GlobalReferenceCount;
        return $"managed={GC.GetTotalMemory(false) / 1024} KiB java={javaUsed / 1024} KiB grefs={grefs}";
    }

    private async Task RunExclusive(string name, Func<Task> body)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            Append($"{name}: skipped, another operation is running");
            return;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Append($"{name}: EXCEPTION {e.GetType().Name}: {e.Message}");
            Log.Error(LogTag, e.ToString());
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Append(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss.fff} {line}";
        Log.Info(LogTag, stamped);
        lock (_logGate)
        {
            _log.AddLast(stamped);
            while (_log.Count > MaxLogLines)
            {
                _log.RemoveFirst();
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        Client.Dispose();
        _facade.Unsubscribe(_transport);
        _facade.Close();
        _transport.Dispose();
    }
}
