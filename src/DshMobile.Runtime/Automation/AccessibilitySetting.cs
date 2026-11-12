namespace DshMobile.Runtime.Automation;

/// <summary>
/// Whether the user enabled an accessibility service in system settings (the app never enables one itself). Reads the
/// raw <c>Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES</c> value the same way the legacy
/// <c>MobileAutomation.isAccessibilityEnabled</c> does: split on ':', each entry parsed with the rules of Android's
/// <c>ComponentName.unflattenFromString</c> (first '/', non-empty class, a class starting with '.' is relative to the
/// package) and compared exactly. Pure function so it is unit-testable off-device (tests/TestVectors).
/// </summary>
public static class AccessibilitySetting
{
    /// <summary>The Relay accessibility service shipped in the DSHBox interlock-relay-core AAR (legacy, ADR-0004).</summary>
    public const string RelayServiceClass = "interlock.relay.core.exec.a11y.RelayAccessibilityService";

    public static bool IsEnabled(string? enabledServices, string packageName, string serviceClass)
    {
        ArgumentNullException.ThrowIfNull(packageName);
        ArgumentNullException.ThrowIfNull(serviceClass);
        if (enabledServices is null)
        {
            return false;
        }
        foreach (var entry in enabledServices.Split(':'))
        {
            if (Unflatten(entry) is var (pkg, cls) && pkg == packageName && cls == serviceClass)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary><c>ComponentName.unflattenFromString</c>: null without a '/' or with nothing after it.</summary>
    public static (string Package, string Class)? Unflatten(string flat)
    {
        ArgumentNullException.ThrowIfNull(flat);
        var sep = flat.IndexOf('/', StringComparison.Ordinal);
        if (sep < 0 || sep + 1 >= flat.Length)
        {
            return null;
        }
        var pkg = flat[..sep];
        var cls = flat[(sep + 1)..];
        if (cls[0] == '.')
        {
            cls = pkg + cls;
        }
        return (pkg, cls);
    }
}
