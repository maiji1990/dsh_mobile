using System.Diagnostics;
using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Layers;

namespace DshMobile.Runtime.Tests;

/// <summary>Runs where a POSIX <c>/bin/sh</c> exists (Linux / macOS); reported as skipped on Windows.</summary>
public sealed class PosixShellFactAttribute : FactAttribute
{
    public PosixShellFactAttribute()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
        {
            Skip = "no POSIX /bin/sh on this machine";
        }
    }
}

/// <summary>
/// vNext PR-07: the guest <c>xdg-user-dir</c> wrapper that DSH's first-use workspace needs (Debian minbase has no
/// xdg-user-dirs). DSH runs <c>xdg-user-dir DOCUMENTS</c> and rejects an empty answer or <c>$HOME</c>; the real-DSH check
/// inside the self-built rootfs is <c>tooling/runtime-smoke.sh</c> (runtime-layers CI).
/// </summary>
public sealed class XdgUserDirShimTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-xdg-").FullName;

    public void Dispose() => LayerInstaller.DeleteTree(_dir);

    private string Shim()
    {
        var layout = new RuntimeLayout(Path.Combine(_dir, "files"), null, "/lib");
        Assert.Equal(3, GuestToolchain.WriteShims(layout));
        Assert.Equal(0, GuestToolchain.WriteShims(layout)); // idempotent
        return Path.Combine(GuestToolchain.ShimBinDir(layout), "xdg-user-dir");
    }

    private string Run(string shim, string? name, string? userDirs = null)
    {
        var home = Path.Combine(_dir, "home");
        Directory.CreateDirectory(Path.Combine(home, ".config"));
        var conf = Path.Combine(home, ".config", "user-dirs.dirs");
        if (userDirs is null)
        {
            File.Delete(conf);
        }
        else
        {
            File.WriteAllText(conf, userDirs);
        }
        var psi = new ProcessStartInfo(shim) { RedirectStandardOutput = true, UseShellExecute = false };
        if (name is not null)
        {
            psi.ArgumentList.Add(name);
        }
        psi.Environment["HOME"] = home;
        psi.Environment.Remove("XDG_CONFIG_HOME");
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(10_000));
        Assert.Equal(0, p.ExitCode);
        return stdout;
    }

    [Fact]
    public void WrittenWrapper_IsTheRepositoryAsset_LfOnly()
    {
        var bytes = File.ReadAllBytes(Shim());
        Assert.Equal(File.ReadAllBytes(Repo.PathOf("src", "DshMobile.Runtime", "Guest", "Assets", GuestToolchain.XdgUserDirName)), bytes);
        Assert.Equal(bytes, GuestToolchain.ReadXdgUserDirShim());
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.StartsWith("#!/bin/sh\n", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [PosixShellFact]
    public void Documents_IsUserData_SoDshGetsAWorkspaceOutsideHome()
    {
        var shim = Shim();
        var home = Path.Combine(_dir, "home");
        Assert.Equal("/root/projects\n", Run(shim, "DOCUMENTS"));
        Assert.Equal(home + "\n", Run(shim, "MUSIC")); // like the real tool: an unset directory is $HOME
        Assert.Equal(home + "\n", Run(shim, null));
        Assert.Equal(home + "\n", Run(shim, "DOCUMENTS;rm -rf /")); // not a name: never reaches sed
    }

    [PosixShellFact]
    public void UserDirsDirs_WinsWhenPresent_WithHomeExpanded()
    {
        var shim = Shim();
        var home = Path.Combine(_dir, "home");
        const string conf = "# written by the user\nXDG_DOCUMENTS_DIR=\"$HOME/Docs\"\nXDG_MUSIC_DIR=\"/srv/music\"\n";
        Assert.Equal(home + "/Docs\n", Run(shim, "DOCUMENTS", conf));
        Assert.Equal("/srv/music\n", Run(shim, "MUSIC", conf));
        Assert.Equal(home + "\n", Run(shim, "PICTURES", conf));
        Assert.Equal("/root/projects\n", Run(shim, "DOCUMENTS", "XDG_MUSIC_DIR=\"/srv/music\"\n"));
    }
}
