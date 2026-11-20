// Ported from DSHBox v1.5.1 (7c9d33c) sandbox-manager/.../DefaultSandboxManager.kt runGuestCommand (argv),
// buildProotEnv, substitutionExports, parseEnvExports, buildGuestPathForDsh, prootBinary / prootLibDir /
// prootLoaderFile (GPL-3.0-or-later). Parity: tests/TestVectors/proot-env.json (env, legacy via reflection) and the
// on-device /proc/<pid>/cmdline comparison of the legacy guest command (argv, docs/COMPATIBILITY.md).

using DshMobile.Runtime.Bundle;

namespace DshMobile.Runtime.Guest;

/// <summary>
/// Builds the host-side PRoot command line and environment exactly like the legacy sandbox-manager, for the
/// configuration this app uses (no share-area binds, no extra binds, no extra PATH entries). Creating the
/// directories PRoot needs as bind sources (npm cache, guest cache, pilot, tmp) is part of building, as in legacy.
/// </summary>
public sealed class ProotCommandBuilder(RuntimeLayout layout)
{
    public const string GuestBash = "/usr/bin/bash";

    private string Current => layout.RuntimeCurrentDir;

    private string BaseRootfs => Path.Combine(Current, "base");

    private string NodeLayerDir => Path.Combine(Current, "node");

    private string DshLayerDir => Path.Combine(Current, "dsh");

    private string ProotSideDir => Path.Combine(Current, "android-side");

    /// <summary><c>prootBinary()</c>: the APK-bundled <c>libproot.so</c>, else the android-side layer's copy.</summary>
    public string ProotBinary
    {
        get
        {
            var bundled = Path.Combine(layout.NativeLibraryDir, "libproot.so");
            return File.Exists(bundled) ? bundled : Path.Combine(ProotSideDir, "bin", "proot");
        }
    }

    /// <summary>LD_LIBRARY_PATH for PRoot: nativeLibraryDir when it has libandroid-shmem.so, else the android-side lib dir.</summary>
    public string ProotLibDir =>
        File.Exists(Path.Combine(layout.NativeLibraryDir, "libandroid-shmem.so")) ? layout.NativeLibraryDir : Path.Combine(ProotSideDir, "lib");

    /// <summary>PROOT_LOADER: the APK-bundled libproot-loader.so, else the android-side loader.</summary>
    public string ProotLoaderFile
    {
        get
        {
            var bundled = Path.Combine(layout.NativeLibraryDir, "libproot-loader.so");
            return File.Exists(bundled) ? bundled : Path.Combine(ProotSideDir, "libexec", "proot", "loader");
        }
    }

    /// <summary>argv of a one-off guest command: <c>proot … /usr/bin/bash -c &lt;command&gt;</c>.</summary>
    public IReadOnlyList<string> GuestCommand(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Directory.CreateDirectory(layout.NpmCacheDir);
        var argv = new List<string>
        {
            ProotBinary,
            "--rootfs=" + BaseRootfs,
            "--bind=/system", "--bind=/apex", "--bind=/proc", "--bind=/dev",
        };
        if (Directory.Exists(NodeLayerDir))
        {
            argv.Add($"--bind={NodeLayerDir}:/usr/local");
        }
        if (Directory.Exists(DshLayerDir))
        {
            argv.Add($"--bind={DshLayerDir}:/opt/dshapp/runtime");
        }
        if (Directory.Exists(layout.ShimDir))
        {
            argv.Add($"--bind={layout.ShimDir}:{RuntimeConstants.LinkShimGuestDir}");
        }
        Directory.CreateDirectory(layout.GuestCacheDir);
        argv.Add($"--bind={layout.GuestCacheDir}:{RuntimeConstants.GuestCacheDir}");
        argv.Add($"--bind={layout.UserDataDir}:{RuntimeConstants.SandboxWorkspace}");
        argv.Add($"--bind={layout.NpmCacheDir}:/root/.npm");
        Directory.CreateDirectory(layout.PilotEntryDir);
        argv.Add($"--bind={layout.PilotEntryDir}:/opt/pilot");
        if (Directory.Exists(layout.BrowserEntryDir))
        {
            argv.Add($"--bind={layout.BrowserEntryDir}:/opt/browser");
        }
        argv.Add("--cwd=/root");
        argv.Add("--kill-on-exit");
        argv.Add(GuestBash);
        argv.Add("-c");
        argv.Add(command);
        return argv;
    }

    /// <summary>
    /// <c>buildProotSandboxCommand</c> as <c>startSandbox()</c> calls it: the keepalive PRoot that keeps the sandbox up.
    /// Its cmdline carries <see cref="RuntimeConstants.SandboxKeepaliveMarker"/>, which is how the tree is found later.
    /// </summary>
    public IReadOnlyList<string> SandboxCommand()
    {
        var argv = LayeredPrefix(shim: null, pilot: null, browser: null, guestCache: null);
        argv.Add("--cwd=/root");
        argv.Add("--kill-on-exit");
        argv.Add(GuestBash);
        argv.Add("-c");
        argv.Add($"echo {RuntimeConstants.SandboxKeepaliveMarker}; trap \"\" HUP INT TERM; while true; do sleep 3600; done");
        return argv;
    }

    /// <summary>
    /// <c>buildProotDshCommand</c> as <c>startDsh()</c> (first start) or the health loop's in-place restart calls it.
    /// The two legacy call sites differ: the restart omits the guest-cache bind (and extra binds), so a DSH that was
    /// restarted automatically runs without <c>/opt/dshmobile-cache</c> (recorded in docs/MIGRATION.md section 7).
    /// </summary>
    public IReadOnlyList<string> DshCommand(bool inPlaceRestart = false)
    {
        var shim = File.Exists(layout.ShimFile) ? layout.ShimDir : null;
        var guestCache = inPlaceRestart ? null : layout.GuestCacheDir;
        // 仅在 guest-cache 已挂载且只读运行层 FS 缓存垫片已落盘时预加载（P0-2，不修改上游 link-shim.mjs）。
        var useStartupFsCacheShim = guestCache is not null && File.Exists(GuestAssetProvisioner.StartupFsCacheShimHostPath(layout));
        Directory.CreateDirectory(layout.PilotEntryDir); // pilotHostDir(): mkdirs, as legacy
        var argv = LayeredPrefix(
            shim,
            layout.PilotEntryDir,
            Directory.Exists(layout.BrowserEntryDir) ? layout.BrowserEntryDir : null,
            guestCache);
        argv.Add("--cwd=/root/projects");
        argv.Add("--kill-on-exit");
        argv.Add("/usr/local/bin/node");
        argv.Add("--expose-internals");
        if (shim is not null)
        {
            argv.Add("--import");
            argv.Add(RuntimeConstants.LinkShimGuestPath);
        }
        if (useStartupFsCacheShim)
        {
            argv.Add("--import");
            argv.Add(GuestAssetProvisioner.GuestStartupFsCacheShim);
        }
        argv.Add(RuntimeConstants.GuestDshBin);
        argv.Add("--profile");
        argv.Add(RuntimeConstants.DshWebProfile);
        foreach (var patch in DshOverlayGuestPaths())
        {
            argv.Add("--patch");
            argv.Add(patch);
        }
        return argv;
    }

    /// <summary>Legacy overlay layers (official-plugin, market, absolute safe mode): only non-empty files are passed.</summary>
    public IReadOnlyList<string> DshOverlayGuestPaths()
    {
        (string Relative, string Guest)[] overlays =
        [
            (".dsh/dshmobile/dsh-official-plugin/dsh-official-plugin-overlay.yml", "/root/projects/.dsh/dshmobile/dsh-official-plugin/dsh-official-plugin-overlay.yml"),
            (".dsh/dshmobile/plugin-market/market-and-safe-mode-overlay.yml", "/root/projects/.dsh/dshmobile/plugin-market/market-and-safe-mode-overlay.yml"),
            (".dsh/dshmobile/safe-mode/absolute-safe-mode.yml", "/root/projects/.dsh/dshmobile/safe-mode/absolute-safe-mode.yml"),
        ];
        return overlays.Where(o =>
        {
            var f = new FileInfo(Path.Combine(layout.UserDataDir, o.Relative));
            return f.Exists && f.Length > 0;
        }).Select(o => o.Guest).ToList();
    }

    /// <summary>
    /// Diagnostics only (docs/STARTUP_EXPERIMENTS.md): the production layered prefix, but with the workspace and
    /// guest-cache binds pointed at experiment directories, so an isolated Web start can never read or write user data.
    /// Production start/stop never calls this.
    /// </summary>
    public IReadOnlyList<string> DiagnosticPrefix(string workspaceDir, string? guestCacheDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspaceDir);
        Directory.CreateDirectory(workspaceDir);
        var argv = new List<string>
        {
            ProotBinary,
            "--rootfs=" + BaseRootfs,
            "--bind=/system", "--bind=/apex", "--bind=/proc", "--bind=/dev",
        };
        if (Directory.Exists(NodeLayerDir))
        {
            argv.Add($"--bind={NodeLayerDir}:/usr/local");
        }
        if (Directory.Exists(DshLayerDir))
        {
            argv.Add($"--bind={DshLayerDir}:/opt/dshapp/runtime");
        }
        if (Directory.Exists(layout.ShimDir))
        {
            argv.Add($"--bind={layout.ShimDir}:{RuntimeConstants.LinkShimGuestDir}");
        }
        if (Directory.Exists(layout.PilotEntryDir))
        {
            argv.Add($"--bind={layout.PilotEntryDir}:/opt/pilot");
        }
        if (Directory.Exists(layout.BrowserEntryDir))
        {
            argv.Add($"--bind={layout.BrowserEntryDir}:/opt/browser");
        }
        if (guestCacheDir is not null)
        {
            Directory.CreateDirectory(guestCacheDir);
            argv.Add($"--bind={guestCacheDir}:{RuntimeConstants.GuestCacheDir}");
        }
        argv.Add($"--bind={workspaceDir}:{RuntimeConstants.SandboxWorkspace}");
        return argv;
    }

    /// <summary><c>layeredProotPrefix</c> (no share-area or extra binds: this app configures none).</summary>
    private List<string> LayeredPrefix(string? shim, string? pilot, string? browser, string? guestCache)
    {
        var argv = new List<string>
        {
            ProotBinary,
            "--rootfs=" + BaseRootfs,
            "--bind=/system", "--bind=/apex", "--bind=/proc", "--bind=/dev",
        };
        if (Directory.Exists(NodeLayerDir))
        {
            argv.Add($"--bind={NodeLayerDir}:/usr/local");
        }
        if (Directory.Exists(DshLayerDir))
        {
            argv.Add($"--bind={DshLayerDir}:/opt/dshapp/runtime");
        }
        if (shim is not null)
        {
            argv.Add($"--bind={shim}:{RuntimeConstants.LinkShimGuestDir}");
        }
        if (pilot is not null)
        {
            argv.Add($"--bind={pilot}:/opt/pilot");
        }
        if (browser is not null)
        {
            argv.Add($"--bind={browser}:/opt/browser");
        }
        if (guestCache is not null)
        {
            argv.Add($"--bind={guestCache}:{RuntimeConstants.GuestCacheDir}");
        }
        argv.Add($"--bind={layout.UserDataDir}:{RuntimeConstants.SandboxWorkspace}");
        return argv;
    }

    /// <summary><c>buildProotEnv(runtimeCurrentDir(), role)</c> for role <c>guest</c> or <c>dsh</c>.</summary>
    public IReadOnlyDictionary<string, string> Environment(string role)
    {
        ArgumentException.ThrowIfNullOrEmpty(role);
        var tmpDir = Path.Combine(Current, "tmp", role);
        Directory.CreateDirectory(tmpDir);
        // Insertion-ordered like Kotlin's mutableMapOf: later puts replace values in place.
        var env = new OrderedEnv
        {
            ["LD_LIBRARY_PATH"] = ProotLibDir,
            ["PROOT_TMP_DIR"] = tmpDir,
            ["PROOT_LOADER"] = ProotLoaderFile,
        };
        var profilePath = Path.Combine(Current, RuntimeBundleLayout.ProfileName);
        var profile = File.Exists(profilePath) ? RuntimeProfile.Parse(File.ReadAllText(profilePath)) : null;
        if (profile is null)
        {
            return env.ToDictionary();
        }
        (string Key, string Value)[] substitutions =
        [
            ("@PROOT_LIB@", ProotLibDir),
            ("@PROOT_LOADER@", ProotLoaderFile),
            ("@PROOT_TMP_DIR@", tmpDir),
            ("@HOME@", "/root"),
            ("@TERM@", "xterm-256color"),
            ("@PATH@", $"{RuntimeConstants.AssetsGuestDir}/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"),
            ("@NODE_BIN@", "/usr/local/bin/node"),
            ("@DSH_PERMISSION_MODE@", "danger-full-access"),
            ("@DSH_BIN@", RuntimeConstants.GuestDshBin),
            ("@DSH_HOME@", "/root/projects/.dsh"),
        ];
        foreach (var name in profile.Assembly)
        {
            if (profile.Layer(name) is not { } layer)
            {
                continue;
            }
            var layerDir = layer.Name switch
            {
                "base" => BaseRootfs,
                "node" => NodeLayerDir,
                "android-side" => ProotSideDir,
                "dsh" => DshLayerDir,
                _ => null,
            };
            if (layerDir is null)
            {
                continue;
            }
            var envFile = Path.Combine(layerDir, layer.EnvFile);
            if (layer.EnvFile.Length == 0 || !File.Exists(envFile))
            {
                continue;
            }
            string src;
            try
            {
                src = File.ReadAllText(envFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var (k, v) in substitutions)
            {
                src = src.Replace(k, v, StringComparison.Ordinal);
            }
            foreach (var (k, v) in ParseEnvExports(src))
            {
                env[k] = v;
            }
        }
        env["TMPDIR"] = "/tmp";
        env["TMP"] = "/tmp";
        env["TEMP"] = "/tmp";
        if (role == "dsh")
        {
            env["DSH_HOME"] = "/root/projects/.dsh";
            env["PORT"] = RuntimeConstants.DshPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var ambient = env.TryGetValue("PATH", out var p) && !string.IsNullOrWhiteSpace(p)
                ? p
                : "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
            env["PATH"] = string.Join(':', new[] { RuntimeConstants.LinkShimGuestDir, RuntimeConstants.AssetsGuestDir + "/bin" }
                .Concat(ambient.Split(':')).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal));
            env["SHELL"] = "/bin/bash";
        }
        return env.ToDictionary();
    }

    /// <summary><c>parseEnvExports</c>: <c>export KEY=VALUE</c> / <c>KEY=VALUE</c> lines, quotes trimmed, last wins.</summary>
    internal static IEnumerable<(string Key, string Value)> ParseEnvExports(string source)
    {
        var result = new OrderedEnv();
        foreach (var raw in source.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            var body = (line.StartsWith("export ", StringComparison.Ordinal) ? line["export ".Length..] : line).Trim();
            var eq = body.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }
            var k = body[..eq].Trim();
            var v = body[(eq + 1)..].Trim().Trim('"').Trim('\'');
            if (k.Length > 0)
            {
                result[k] = v;
            }
        }
        return result.Select(kv => (kv.Key, kv.Value));
    }

    /// <summary>Insertion-ordered string map (replacing a value keeps its position, like LinkedHashMap).</summary>
    private sealed class OrderedEnv : IEnumerable<KeyValuePair<string, string>>
    {
        private readonly List<string> _order = [];
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string this[string key]
        {
            get => _values[key];
            set
            {
                if (!_values.ContainsKey(key))
                {
                    _order.Add(key);
                }
                _values[key] = value;
            }
        }

        public bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);

        public Dictionary<string, string> ToDictionary() => _order.ToDictionary(k => k, k => _values[k], StringComparer.Ordinal);

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _order.Select(k => new KeyValuePair<string, string>(k, _values[k])).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
