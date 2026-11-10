using Android.Content;
using Android.OS;

namespace DshMobile.App.Browser;

/// <summary>
/// Never started. Its only job is to name the <c>:browser</c> process on a managed component so the .NET build
/// generates a <c>MonoRuntimeProvider</c> for that process (it does so only for processes declared on C# components).
/// DSHBox's browser runtime components come from a library manifest; without this, Android would start the
/// <c>:browser</c> process with our managed <see cref="DshMobileApplication"/> but no .NET runtime (ADR-0005).
/// </summary>
[Service(Process = ":browser", Exported = false, Enabled = false)]
public sealed class BrowserProcessAnchor : Service
{
    public override IBinder? OnBind(Intent? intent) => null;
}
