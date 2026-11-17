// Ported from this repo's native/native-bridge/src/main/java/dshmobile/nativebridge/GuestToolchain.kt, itself ported from
// DSHBox v1.5.1 SandboxService (provisionCorepackCache, DSH_CLI_SHIM, PNPM_CLI_SHIM; GPL-3.0-or-later).
// The pnpm wrapper now runs the pinned package directly. Registered in docs/UPSTREAMS.md.

namespace DshMobile.Runtime.Guest;

/// <summary>
/// Makes <c>dsh</c> and <c>pnpm</c> usable inside the guest (TASK-009). The wrappers live in
/// <c>user-data/.dsh/dshmobile/bin</c>, which the DSH role puts on the guest PATH.
/// The bundled pnpm 11.1.2 runs directly on Node. Its archived Corepack cache is unpacked into guest
/// <c>/opt/dshmobile-cache/corepack</c>. Not ported (as legacy): DSHBox's apt/dpkg wrappers and the opencode wrapper.
/// </summary>
public static class GuestToolchain
{
    public const string AssetDir = "toolchain-cache";
    public const string CorepackAsset = "corepack-pnpm-11.1.2.tar.zst";
    public const string ReadyMarker = ".ready-sha256";
    internal const string PnpmEntryPoint = "v1/pnpm/11.1.2/bin/pnpm.mjs";

    /// <summary>Host directory of the corepack cache (guest <c>/opt/dshmobile-cache/corepack</c>).</summary>
    public static string CorepackDir(RuntimeLayout layout) => Path.Combine(layout.GuestCacheDir, "corepack");

    /// <summary>Host directory of the wrappers (guest <c>/root/projects/.dsh/dshmobile/bin</c>).</summary>
    public static string ShimBinDir(RuntimeLayout layout) => Path.Combine([layout.UserDataDir, .. RuntimeConstants.AssetsRelativeDir.Split('/'), "bin"]);

    /// <summary>Same as DSHBox's DSH_CLI_SHIM: official CLI from the managed DSH layer, with the hard-link shim preloaded.</summary>
    public const string DshShim = """
        #!/bin/sh
        # DSH Mobile: `dsh` = the app-managed DSH layer's official CLI (same version and profiles as the WebUI).
        # The hard-link shim is required: DSH publishes files with link(), which Android's data partition refuses.
        # Note: `dsh web` would start a second web server and collide with the app's port 3080.
        [ -n "$DSH_HOME" ] || DSH_HOME=/root/projects/.dsh
        export DSH_HOME
        BIN="${DSH_BIN:-/opt/dshapp/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js}"
        SHIM=""
        [ -f /opt/dshmobile/link-shim.mjs ] && SHIM="--import /opt/dshmobile/link-shim.mjs"
        exec /usr/local/bin/node --expose-internals $SHIM "$BIN" "$@"
        """;

    /// <summary>
    /// Runs the bundled pnpm directly on Node, including Node versions without Corepack. The store stays where the profile
    /// already recorded it (pnpm compares storeDir as a string and fails with ERR_PNPM_UNEXPECTED_STORE otherwise), copy
    /// imports (hard links / clones fail with EACCES on Android), registry left to the caller.
    /// </summary>
    public const string PnpmShim = $$"""
        #!/bin/sh
        # DSH Mobile: use the pinned pnpm package from the APK cache.
        PNPM="/opt/dshmobile-cache/corepack/{{PnpmEntryPoint}}"
        if [ ! -f "$PNPM" ]; then
          printf '%s\n' 'dshmobile: bundled pnpm entry point is missing' >&2
          exit 127
        fi
        STORE=""
        if [ -f ./.modules.yaml ]; then
          RECORDED=$(sed -n 's/.*"storeDir"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' ./.modules.yaml 2>/dev/null | head -n 1)
          if [ -z "$RECORDED" ]; then
            RECORDED=$(sed -n 's/^storeDir:[[:space:]]*//p' ./.modules.yaml 2>/dev/null | head -n 1)
          fi
          if [ -n "$RECORDED" ] && [ -d "$RECORDED" ]; then
            case "$RECORDED" in
              */v[0-9]*) STORE=${RECORDED%/*} ;;
              *) STORE="$RECORDED" ;;
            esac
          fi
        fi
        if [ -z "$STORE" ]; then
          if [ -d /opt/dshmobile-cache ]; then
            STORE=/opt/dshmobile-cache/pnpm-store
          else
            STORE=/root/projects/.dsh/pnpm-store
          fi
        fi
        export pnpm_config_store_dir="$STORE"
        export pnpm_config_package_import_method=copy
        mkdir -p "$STORE" 2>/dev/null || true
        exec /usr/local/bin/node "$PNPM" "$@"
        """;

    /// <summary>
    /// vNext PR-07 (<c>Guest/Assets/xdg-user-dir</c>): Debian minbase has no <c>xdg-user-dirs</c>, and DSH's first-use workspace
    /// (<c>@deepseek-ai/dsh-api-workspace-controller</c> <c>defaultWorkspaceDirectory</c>) runs <c>xdg-user-dir DOCUMENTS</c>
    /// on Linux and fails with "system Documents directory is unavailable" without it. Same contract as the real tool:
    /// honour <c>user-dirs.dirs</c>, else Documents = <c>/root/projects</c> (user-data, so the default workspace lands in
    /// <c>/root/projects/deepseek-harness/default-workspace</c>), every other name = <c>$HOME</c>. No DSH code is changed.
    /// </summary>
    public const string XdgUserDirName = "xdg-user-dir";

    private const string XdgUserDirResource = "DshMobile.Guest.xdg-user-dir";

    /// <summary>The embedded bytes of <c>Guest/Assets/xdg-user-dir</c> (the same file <c>tooling/runtime-smoke.sh</c> stages).</summary>
    public static byte[] ReadXdgUserDirShim()
    {
        using var stream = typeof(GuestToolchain).Assembly.GetManifestResourceStream(XdgUserDirResource)
            ?? throw new InvalidOperationException($"embedded guest asset missing: {XdgUserDirResource}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Legacy <c>setExecutable(true, false)</c> on an app-private file: owner rw + execute for everyone.</summary>
    internal const UnixFileMode ShimMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>
    /// Writes the <c>dsh</c>, <c>pnpm</c> and <c>xdg-user-dir</c> wrappers (legacy rewrites them every process; C# only when the text differs,
    /// and always fixes the mode). Returns how many were written. Throws on an unwritable directory.
    /// </summary>
    public static int WriteShims(RuntimeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var bin = ShimBinDir(layout);
        Directory.CreateDirectory(bin);
        var written = 0;
        static byte[] Text(string body) => System.Text.Encoding.UTF8.GetBytes(body.ReplaceLineEndings("\n") + "\n");
        foreach (var (name, bytes) in new[] { ("dsh", Text(DshShim)), ("pnpm", Text(PnpmShim)), (XdgUserDirName, ReadXdgUserDirShim()) })
        {
            var path = Path.Combine(bin, name);
            if (GuestAssetProvisioner.WriteIfDifferent(path, bytes))
            {
                written++;
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, ShimMode);
            }
        }
        return written;
    }

    /// <summary>
    /// Unpacks the APK's corepack cache into <see cref="CorepackDir"/> unless the ready marker already names the asset's
    /// SHA-256 (legacy idempotence). The tarball is checked against its <c>.sha256</c> sidecar before anything is
    /// replaced; extraction uses the C# <see cref="Layers.LayerExtractor"/> (legacy <c>BundleManager.extractTarGz</c>)
    /// into a private stage that is renamed into place. <c>Changed</c>: the cache was (re)unpacked; <c>Error</c>: why it
    /// could not be (the previous cache, if any, is left as it was).
    /// </summary>
    public static async Task<(bool Changed, string? Error)> ProvisionCorepackAsync(RuntimeLayout layout, Layers.IBundledAssets assets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(assets);
        string expected;
        try
        {
            using var sidecar = new StreamReader(assets.Open($"{AssetDir}/{CorepackAsset}.sha256"));
            expected = (await sidecar.ReadToEndAsync(ct).ConfigureAwait(false)).Trim().Split(' ')[0].Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (false, $"corepack cache asset missing: {e.Message}");
        }
        var cache = CorepackDir(layout);
        var marker = Path.Combine(cache, ReadyMarker);
        if (File.Exists(Path.Combine(cache, PnpmEntryPoint)) && File.Exists(marker) && File.ReadAllText(marker).Trim() == expected)
        {
            return (false, null);
        }
        var stage = Path.Combine(layout.GuestCacheDir, $"corepack-stage-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        try
        {
            Directory.CreateDirectory(stage);
            var tarball = Path.Combine(stage, CorepackAsset);
            using (var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256))
            {
                var input = assets.Open($"{AssetDir}/{CorepackAsset}");
                await using (input.ConfigureAwait(false))
                {
                    var output = new FileStream(tarball, FileMode.CreateNew, FileAccess.Write);
                    await using (output.ConfigureAwait(false))
                    {
                        var buffer = new byte[1 << 16];
                        int n;
                        while ((n = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            hash.AppendData(buffer, 0, n);
                            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                        }
                    }
                }
                if (!Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "corepack cache sha256 mismatch");
                }
            }
            var unpacked = Path.Combine(stage, "out");
            var extracted = await Layers.LayerExtractor.ExtractAsync(tarball, new Layers.DiskExtractionTarget(unpacked), ct).ConfigureAwait(false);
            if (!extracted.Ok)
            {
                return (false, $"corepack cache extraction failed: {extracted.Code}: {extracted.Message}");
            }
            if (!File.Exists(Path.Combine(unpacked, PnpmEntryPoint)))
            {
                return (false, "bundled pnpm entry point is missing from the corepack cache");
            }
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
            Directory.Move(unpacked, cache);
            await File.WriteAllTextAsync(marker, expected, ct).ConfigureAwait(false);
            return (true, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (false, $"corepack cache provisioning failed: {e.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(stage))
                {
                    Directory.Delete(stage, recursive: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
