using Android.Content.Res;
using DshMobile.Runtime.Layers;

namespace DshMobile.App.Bridge;

/// <summary>
/// The APK assets for the guest-asset provisioner (<c>assets/dshmobile</c>, <c>assets/toolchain-cache</c>; the bundled
/// DSH layer under <c>assets/dsh</c> is gone since ADR-0027). Read-only; <c>.zst</c> assets are stored uncompressed
/// (csproj), so they stream without an extra copy into the cache.
/// </summary>
internal sealed class AndroidBundledAssets(AssetManager assets) : IBundledAssets
{
    public IReadOnlyList<string> List(string dir)
    {
        try
        {
            return assets.List(dir) ?? [];
        }
        catch (Java.IO.IOException e)
        {
            throw new IOException($"cannot list assets/{dir}: {e.Message}", e);
        }
    }

    public Stream Open(string path)
    {
        try
        {
            return assets.Open(path, Access.Streaming);
        }
        catch (Java.IO.IOException e)
        {
            throw new IOException($"cannot open assets/{path}: {e.Message}", e);
        }
    }
}
