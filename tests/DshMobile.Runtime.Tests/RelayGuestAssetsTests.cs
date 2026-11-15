// Switch slice (ADR-0010): the guest-visible Relay files the C# host publishes must be the ones the legacy Relay
// publishes. Locked against files the legacy Relay wrote on the acceptance device (Honor PTP-AN10, Android 17,
// 2026-11-14; tests/TestVectors/relay-guest/) and against values of the upstream sources (DSHBox v1.5.1 @ 7c9d33c,
// pinned when PR-10 removed the submodule).

using System.Security.Cryptography;
using System.Text;
using DshMobile.Runtime.Automation.Relay;
using DshMobile.Runtime.Automation.Relay.Logging;
using DshMobile.Runtime.Automation.Relay.Storage;
using DshMobile.Runtime.Automation.Relay.Transport;

namespace DshMobile.Runtime.Tests;

public sealed class RelayGuestAssetsTests : IDisposable
{
    /// <summary>The legacy Relay's version.json on the device: the hashes of what it published.</summary>
    private const string DeviceScriptHash = "9cb271f6a1adb50509ebc0293c9f9947fb2c09b71b5c0968942dfbdba0d8ff9b";
    private const string DeviceUsageHash = "40470726a2ec2dce817033ebaa171d728fac2046804edd042559e46d7fe96dd8";
    /// <summary>SHA-256 of interlock-relay-core <c>assets/relay/relay-client.cjs</c> at DSHBox 7c9d33c.</summary>
    private const string UpstreamScriptHash = "9cb271f6a1adb50509ebc0293c9f9947fb2c09b71b5c0968942dfbdba0d8ff9b";
    /// <summary>SHA-256 of <c>GuestAssets.kt</c> <c>USAGE_TEMPLATE</c> after Kotlin's trimIndent, at DSHBox 7c9d33c.</summary>
    private const string UpstreamUsageTemplateHash = "e655efd9db93a8b81ab305087db1779ef0a6fe86a204bc07b25aa3e533b2e26f";
    private const string DeviceCapabilitiesHash = "50e5ea8eabe42e330acf65736620311d8b2ab7643cde8ffb8eca9be81de5734c";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay-guest-" + Guid.NewGuid().ToString("N"));

    public RelayGuestAssetsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static string Sha(string s) => Sha(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void Embedded_entry_script_is_the_upstream_asset_byte_for_byte()
    {
        var embedded = RelayGuestAssetFiles.Load(RelayPathPolicy.DefaultAssetClient)!;
        Assert.Equal(DeviceScriptHash, Sha(embedded));
        Assert.Equal(UpstreamScriptHash, Sha(embedded));
        Assert.Null(RelayGuestAssetFiles.Load("relay/other.cjs"));
    }

    [Fact]
    public void Embedded_usage_template_is_upstreams_trimmed_raw_string()
    {
        Assert.Equal(UpstreamUsageTemplateHash, Sha(RelayGuestAssetFiles.UsageTemplate()));
    }

    [Fact]
    public void Usage_for_the_app_paths_is_what_the_legacy_relay_published()
    {
        var paths = new RelayPaths(_root, RelayPathPolicy.DshMobile);

        Assert.Equal(DeviceUsageHash, Sha(GuestAssets.UsageText(paths)));
    }

    /// <summary>
    /// The device listing rebuilt from the facts that produced it: accessibility bound, Shizuku running, granted and its
    /// user service bound (uid 2000, v13), no trusted display, no notification listener, only the install and
    /// notification permissions granted, microphone blocked, tiers at their defaults, no surface preference.
    /// </summary>
    [Fact]
    public void Capabilities_json_is_byte_identical_to_the_device_listing()
    {
        var facts = new RelaySystemFacts(37, A11yBound: true, A11yListed: true, ShellAvailable: true, TrustedDisplayAvailable: false,
            NotificationListener: false, new HashSet<string>(StringComparer.Ordinal) { "android.permission.REQUEST_INSTALL_PACKAGES", "android.permission.POST_NOTIFICATIONS" },
            MicrophoneBlocked: true);
        var backend = new RelayBackendFacts(37, ShellAvailable: true, TrustedDisplayAlive: false);
        var policy = SurfacePolicy.For(RelaySurfaces.ParsePreference(null), backend);
        var tiers = new RelayTierStore(_ => null);
        var rows = RelayCapabilityListing.Rows(policy, (w, r) => RelaySystemProbe.Probe(w, r, facts), tiers.TierOf,
            (w, r) => RelayBackendSupport.CanServe(w, r, backend), w => RelayBackendSupport.IsImplemented(w, backend));
        var paths = new RelayPaths(_root, RelayPathPolicy.DshMobile);

        var json = RelayCapabilityListing.Json(rows, paths.GuestScript, "org.example.dshmobile", new ShizukuSnapshot(true, true, 2000, 13),
            bound: true, new TrustedDisplayState(false, -1, "untested"));

        var device = File.ReadAllText(Repo.PathOf("tests", "TestVectors", "relay-guest", "capabilities.PTP-AN10-A17.json"), Encoding.UTF8);
        Assert.Equal(DeviceCapabilitiesHash, Sha(device));
        Assert.Equal(device, json);
    }

    [Fact]
    public void Catalog_lists_every_capability_once_in_registry_order()
    {
        var wires = RelayCapabilityCatalog.All.Select(e => e.Wire).ToList();
        Assert.Equal(wires.Count, wires.Distinct().Count());
        Assert.Equal(RelayProtocol.Capabilities.Order(StringComparer.Ordinal), wires.Order(StringComparer.Ordinal));
        // Registry (listing) order differs from the declaration order the envelope uses.
        Assert.Equal("ui.click", wires[7]);
        Assert.Equal("notify.post", wires[^1]);
    }

    [Theory]
    [InlineData("SessionConsent", "SESSION_CONSENT")]
    [InlineData("UnavailableOnDevice", "UNAVAILABLE_ON_DEVICE")]
    [InlineData("Any", "ANY")]
    public void Enum_names_are_written_the_kotlin_way(string csharp, string kotlin) =>
        Assert.Equal(kotlin, RelayCapabilityListing.Screaming(csharp));

    [Fact]
    public void Indented_writer_matches_libcore_for_empty_and_nested_values()
    {
        var node = new System.Text.Json.Nodes.JsonObject
        {
            ["a"] = new System.Text.Json.Nodes.JsonArray(),
            ["b"] = new System.Text.Json.Nodes.JsonObject(),
            ["c"] = new System.Text.Json.Nodes.JsonArray("x/y", 1),
        };

        Assert.Equal("{\n  \"a\": [],\n  \"b\": {},\n  \"c\": [\n    \"x\\/y\",\n    1\n  ]\n}", LibcoreJson.Indented(node, 2));
    }

    private GuestAssets NewAssets(out RelayPaths paths)
    {
        paths = new RelayPaths(_root, RelayPathPolicy.DshMobile);
        return new GuestAssets(paths, new RunLog(paths.LogsDir));
    }

    [Fact]
    public void Materialize_publishes_three_files_then_a_marker_of_their_hashes()
    {
        var assets = NewAssets(out var paths);

        Assert.False(assets.Intact());
        Assert.True(assets.Materialize("{\"x\":1}\n"));

        Assert.Equal(DeviceScriptHash, Sha(File.ReadAllBytes(paths.ClientScript)));
        Assert.Equal("{\"x\":1}\n", File.ReadAllText(paths.CapabilitiesFile));
        Assert.Equal(DeviceUsageHash, Sha(File.ReadAllBytes(paths.UsageFile)));
        var marker = File.ReadAllText(paths.VersionMarkerFile);
        Assert.Matches("^\\{\"layout\":1,\"files\":\\{\"bin/relay\":\"" + DeviceScriptHash + "\",\"capabilities.json\":\"" + Sha("{\"x\":1}\n")
            + "\",\"USAGE.md\":\"" + DeviceUsageHash + "\"\\},\"publishedAtWall\":\\d+\\}$", marker);
        Assert.True(assets.Intact());
        // Nothing is left in the staging directory, and nothing staged went into the bound tree.
        Assert.Empty(Directory.GetFiles(paths.StagingDir));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(paths.ClientScript).HasFlag(UnixFileMode.OtherExecute));
        }
    }

    [Fact]
    public void Intact_fails_on_any_rewritten_or_missing_file_or_marker()
    {
        var assets = NewAssets(out var paths);
        Assert.True(assets.Materialize("{}\n"));

        File.AppendAllText(paths.UsageFile, "tampered");
        Assert.False(assets.Intact());
        Assert.True(assets.Materialize("{}\n"));
        Assert.True(assets.Intact());

        File.Delete(paths.ClientScript);
        Assert.False(assets.Intact());
        Assert.True(assets.Materialize("{}\n"));

        File.WriteAllText(paths.VersionMarkerFile, "{\"layout\":2,\"files\":{}}");
        Assert.False(assets.Intact());
        File.WriteAllText(paths.VersionMarkerFile, "not json");
        Assert.False(assets.Intact());
    }

    [Fact]
    public void Materialize_fails_closed_without_the_packaged_script()
    {
        var paths = new RelayPaths(_root, RelayPathPolicy.DshMobile);
        var assets = new GuestAssets(paths, new RunLog(paths.LogsDir), assetLoader: _ => null);

        Assert.False(assets.Materialize("{}\n"));
        Assert.False(File.Exists(paths.VersionMarkerFile));
    }

    [Fact]
    public void Publish_refuses_a_link_the_guest_put_on_the_way()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // creating links needs privileges on Windows; the rule itself is RelayStorageRules (tested there)
        }
        var assets = NewAssets(out var paths);
        Assert.True(assets.Materialize("{}\n"));
        var outside = Path.Combine(_root, "secret");
        Directory.CreateDirectory(outside);
        Directory.Delete(paths.BinDir, recursive: true);
        Directory.CreateSymbolicLink(paths.BinDir, outside);

        Assert.False(assets.Materialize("{}\n"));
        Assert.Empty(Directory.GetFiles(outside));
        Assert.False(assets.Intact());
    }

}
