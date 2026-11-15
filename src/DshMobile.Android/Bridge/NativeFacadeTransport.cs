using DshMobile.Core.Bridge;
using DshMobile.NativeBridge;

namespace DshMobile.App.Bridge;

/// <summary>
/// JNI adapter: one Java peer object serves as the callback for every request (a single GREF,
/// not one per call). Responses are correlated by requestId inside <see cref="BridgeClient"/>.
/// </summary>
public sealed class NativeFacadeTransport : Java.Lang.Object, INativeCallback, IBridgeTransport
{
    private readonly DshNativeFacade _facade;
    private readonly Action _ensureInitialized;
    private IBridgeSink? _sink;

    /// <param name="ensureInitialized">Creates the legacy facade on first use (BridgeHost: only explicit fallbacks call it).</param>
    public NativeFacadeTransport(DshNativeFacade facade, Action ensureInitialized)
    {
        _facade = facade;
        _ensureInitialized = ensureInitialized;
    }

    public void Attach(IBridgeSink sink)
    {
        _sink = sink;
        _facade.Subscribe(this);
    }

    public void Send(string requestJson)
    {
        _ensureInitialized();
        _facade.Execute(requestJson, this);
    }

    /// <summary>Called by Java on the bridge worker thread. Must not throw or block.</summary>
    public void OnEvent(string? json)
    {
        if (json is not null)
        {
            _sink?.OnInbound(json);
        }
    }
}
