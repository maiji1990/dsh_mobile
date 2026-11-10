using DshMobile.Application;

namespace DshMobile.App.Bridge;

/// <summary><see cref="IForegroundServiceController"/> backed by <see cref="HarnessForegroundService"/>.</summary>
internal sealed class AndroidForegroundService(Android.Content.Context context) : IForegroundServiceController
{
    public void Acquire() => HarnessForegroundService.Start(context);

    public void Release() => HarnessForegroundService.Stop(context);
}
