// Ported from DSHBox terminal-session (GPL-3.0), commit 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4:
//   terminal-session/src/main/java/com/dshbox/terminal/TerminalCommandFactory.kt (sandboxLoginShell, LOGIN_FLAGS)
//   terminal-session/src/main/java/com/dshbox/terminal/TerminalEnvFactory.kt     (sandboxEnv, buildGuestPath)
//   terminal-session/src/main/java/com/dshbox/terminal/TerminalProcFake.kt       (ensureBindArgs and the file texts)
//   terminal-session/src/main/java/com/dshbox/terminal/TerminalPathsResolver.kt  (resolve: which host dirs exist)
// Registered in docs/UPSTREAMS.md. Scope = what this app's legacy TerminalController uses: no overlay init snippet,
// no share binds, no capability binds / PATH entries (all empty there). The legacy argv is the shadow baseline.

using System.Text;

namespace DshMobile.Runtime.Guest;

/// <summary>Argv and environment for the guest login shell used by the PTY self-test.</summary>
public sealed class TerminalCommandBuilder(RuntimeLayout layout)
{
    /// <summary><c>LOGIN_FLAGS</c>: <c>--noprofile</c> keeps the base image's /etc/profile from replacing PATH.</summary>
    public static readonly IReadOnlyList<string> LoginFlags = ["--noprofile", "--login"];

    private readonly ProotCommandBuilder _proot = new(layout);

    private string Current => layout.RuntimeCurrentDir;

    /// <summary>The rootfs: layered <c>base</c>, else a legacy single <c>debian</c> rootfs, else null (not installed).</summary>
    public string? Rootfs
    {
        get
        {
            var baseLayer = Path.Combine(Current, "base");
            var legacy = Path.Combine(Current, "debian");
            return Directory.Exists(baseLayer) ? baseLayer : Directory.Exists(legacy) ? legacy : null;
        }
    }

    public string ProotTmpDir => Path.Combine(layout.RuntimeDir, "tmp", "terminal");

    /// <summary>
    /// <c>sandboxLoginShell(paths, null, procBinds)</c>. Like upstream it creates the guest-cache / pilot / browser
    /// host dirs and the fabricated <c>/proc</c> sources before returning. Null when no runtime is installed.
    /// </summary>
    public IReadOnlyList<string>? LoginShell(Func<string, bool>? hostDirectoryExists = null)
    {
        var rootfs = Rootfs;
        // Upstream resolve() returns null without a rootfs or without a PRoot binary (bundled or android-side).
        if (rootfs is null || !File.Exists(_proot.ProotBinary))
        {
            return null;
        }
        var exists = hostDirectoryExists ?? Directory.Exists;
        var argv = new List<string>
        {
            _proot.ProotBinary,
            "--kill-on-exit",
            "-0",
            "--rootfs=" + rootfs,
            "--bind=/system",
            "--bind=/apex",
            "--bind=/proc",
            "--bind=/dev",
        };
        var node = Path.Combine(Current, "node");
        if (Directory.Exists(node))
        {
            argv.Add($"--bind={node}:/usr/local");
        }
        var dsh = Path.Combine(Current, "dsh");
        if (Directory.Exists(dsh))
        {
            argv.Add($"--bind={dsh}:/opt/dshapp/runtime");
        }
        if (Directory.Exists(layout.ShimDir))
        {
            argv.Add($"--bind={layout.ShimDir}:/opt/dshmobile");
        }
        Directory.CreateDirectory(layout.GuestCacheDir);
        argv.Add($"--bind={layout.GuestCacheDir}:/opt/dshmobile-cache");
        Directory.CreateDirectory(layout.PilotEntryDir);
        argv.Add($"--bind={layout.PilotEntryDir}:/opt/pilot");
        Directory.CreateDirectory(layout.BrowserEntryDir);
        argv.Add($"--bind={layout.BrowserEntryDir}:/opt/browser");
        argv.AddRange(ProcFakeBinds(rootfs));
        if (exists("/linkerconfig"))
        {
            argv.Add("--bind=/linkerconfig");
        }
        argv.Add($"--bind={layout.UserDataDir}:{RuntimeConstants.SandboxWorkspace}");
        argv.Add("--cwd=/root");
        argv.Add(ProotCommandBuilder.GuestBash);
        argv.AddRange(LoginFlags);
        return argv;
    }

    /// <summary><c>sandboxEnv(paths)</c>: the child's complete environment (the PTY child gets nothing else), in order.</summary>
    public IReadOnlyList<string> Environment()
    {
        Directory.CreateDirectory(ProotTmpDir);
        return
        [
            "LD_LIBRARY_PATH=" + _proot.ProotLibDir,
            "PROOT_LOADER=" + _proot.ProotLoaderFile,
            "PROOT_TMP_DIR=" + ProotTmpDir,
            "HOME=/root",
            "USER=root",
            "LOGNAME=root",
            "PATH=" + GuestPath,
            "SHELL=/usr/bin/bash",
            "TERM=xterm-256color",
            "LANG=C.UTF-8",
            "COLORTERM=truecolor",
            @"PS1=\u@\h:\w\$ ",
            "TMPDIR=/tmp",
            "PWD=/root",
            "DSH_HOME=/root/projects/.dsh",
            "DSH_BIN=" + RuntimeConstants.GuestDshBin,
            "DSH_PERMISSION_MODE=danger-full-access",
        ];
    }

    /// <summary><c>buildGuestPath</c> with no capability entries.</summary>
    public const string GuestPath = "/root/projects/.dsh/dshmobile/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/system/bin:/system/xbin";

    /// <summary>
    /// <c>TerminalProcFake.ensureBindArgs</c>: Android denies /proc/stat, /proc/uptime, /proc/loadavg to apps, which
    /// crashes htop; static readable stand-ins are bound over them. Existing non-empty files are left alone.
    /// </summary>
    public static IReadOnlyList<string> ProcFakeBinds(string rootfs)
    {
        var dir = Path.Combine(rootfs, "root", ".procfake");
        Directory.CreateDirectory(dir);
        (string Name, string Text)[] files = [("stat", Stat), ("uptime", Uptime), ("loadavg", LoadAvg)];
        foreach (var (name, text) in files)
        {
            var f = new FileInfo(Path.Combine(dir, name));
            if (!f.Exists || f.Length == 0)
            {
                File.WriteAllText(f.FullName, text, new UTF8Encoding(false));
            }
        }
        return files.Select(f => $"--bind={Path.Combine(dir, f.Name)}:/proc/{f.Name}").ToList();
    }

    public static readonly string Stat = BuildStat();

    public const string Uptime = "100000.00 500000.00\n";

    public const string LoadAvg = "0.00 0.01 0.05 1/100 1000\n";

    private static string BuildStat()
    {
        var sb = new StringBuilder("cpu  1000 0 1000 1000000 0 0 0 0 0 0\n");
        for (var i = 0; i < 8; i++)
        {
            sb.Append("cpu").Append(i).Append(" 125 0 125 125000 0 0 0 0 0 0\n");
        }
        sb.Append("intr 0 0 0\nctxt 0\nbtime 0\nprocesses 0\nprocs_running 0\nprocs_blocked 0\nsoftirq 0 0 0 0\n");
        return sb.ToString();
    }
}
