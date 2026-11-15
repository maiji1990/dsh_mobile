// Directory layout ported from DSHBox v1.5.1 sandbox-manager SandboxConfig.kt (GPL-3.0-or-later).
// The C# runtime must find the exact same directories the legacy runtime wrote, so existing user data, sessions
// and layers survive the migration (migration plan section 8: never lose user data).

namespace DshMobile.Runtime;

/// <summary>Host-side directory layout of the runtime under the app's private storage.</summary>
public sealed class RuntimeLayout
{
    public RuntimeLayout(string filesDir, string? cacheDir, string nativeLibraryDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(filesDir);
        ArgumentException.ThrowIfNullOrEmpty(nativeLibraryDir);
        FilesDir = filesDir;
        CacheDir = cacheDir;
        NativeLibraryDir = nativeLibraryDir;
    }

    public string FilesDir { get; }

    public string? CacheDir { get; }

    /// <summary>The only directory executables may be started from (W^X, see DshMobile.Native).</summary>
    public string NativeLibraryDir { get; }

    public string RuntimeDir => Path.Combine(FilesDir, "runtime");

    /// <summary>Active layers (<c>runtime/runtime-current/{base,node,android-side,dsh}</c>).</summary>
    public string RuntimeCurrentDir => Path.Combine(RuntimeDir, "runtime-current");

    public string SandboxDir => Path.Combine(FilesDir, "sandbox");

    /// <summary>User workspace, guest <c>/root/projects</c>. Never deleted by runtime operations.</summary>
    public string UserDataDir => Path.Combine(FilesDir, "user-data");

    public string LogsDir => Path.Combine(FilesDir, "logs");

    public string BackupsDir => Path.Combine(FilesDir, "backups");

    public string UpdatesDir => Path.Combine(FilesDir, "updates");

    public string NpmCacheDir => Path.Combine(CacheDir ?? FilesDir, "npm-cache");

    public string GuestCacheDir => Path.Combine(FilesDir, RuntimeConstants.GuestCacheDirLeaf);

    public string ShimDir => Path.Combine(FilesDir, "dshmobile");

    public string ShimFile => Path.Combine(ShimDir, "link-shim.mjs");

    public string BrowserEntryDir => Path.Combine(FilesDir, "browser-tools");

    public string PilotEntryDir => Path.Combine(FilesDir, "pilot");

    /// <summary>C#-runtime private state (journal); not part of the legacy layout, never bound into the guest.</summary>
    public string HostStateDir => Path.Combine(FilesDir, "dshmobile-runtime");

    public string JournalFile => Path.Combine(HostStateDir, "runtime-journal.jsonl");

    public string LayerDir(string layer) => Path.Combine(RuntimeCurrentDir, layer);

    /// <summary>
    /// Directories that hold user data (workspace, sessions, credentials) and must survive any runtime install,
    /// rollback or cleanup. Destructive runtime code checks targets against this list.
    /// </summary>
    public IReadOnlyList<string> ProtectedUserDirs => [UserDataDir, BackupsDir];
}
