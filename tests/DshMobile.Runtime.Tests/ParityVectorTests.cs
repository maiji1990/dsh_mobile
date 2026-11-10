using DshMobile.Runtime.Bundle;
using DshMobile.Runtime.Guest;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// The C# half of the migration contract: the same tests/TestVectors files are run by the legacy Kotlin/DSHBox
/// implementation (native-bridge PluginSpecTest + LegacyRuntimeParityTest). Both green = both agree on every vector.
/// </summary>
public class ParityVectorTests
{
    [Fact]
    public void Versions_Compare_MatchesGoldenVectors()
    {
        var cases = Repo.Vectors("versions.json").GetProperty("compare").EnumerateArray().ToList();
        Assert.NotEmpty(cases);
        foreach (var v in cases)
        {
            var a = v.GetProperty("a").GetString()!;
            var b = v.GetProperty("b").GetString()!;
            Assert.True(v.GetProperty("sign").GetInt32() == Math.Sign(Versions.Compare(a, b)), $"compare({a}, {b})");
            // Antisymmetry is a property, not a vector: check it on every pair too.
            Assert.True(Math.Sign(Versions.Compare(a, b)) == -Math.Sign(Versions.Compare(b, a)), $"antisymmetry {a} / {b}");
        }
    }

    [Fact]
    public void LayerOfArchiveName_MatchesGoldenVectors()
    {
        foreach (var v in Repo.Vectors("runtime-bundle-layout.json").GetProperty("layerOfArchiveName").EnumerateArray())
        {
            var name = v.GetProperty("name").GetString()!;
            Assert.True(v.StringOrNull("layer") == RuntimeBundleLayout.LayerOfArchiveName(name), name);
        }
    }

    [Fact]
    public void Analyze_MatchesGoldenVectors()
    {
        var cases = Repo.Vectors("runtime-bundle-layout.json").GetProperty("analyze").EnumerateArray().ToList();
        Assert.NotEmpty(cases);
        foreach (var v in cases)
        {
            var label = v.GetProperty("name").GetString();
            var entries = v.GetProperty("entries").EnumerateArray().Select(e => e.GetString()!).ToList();
            var r = RuntimeBundleLayout.Analyze(entries);
            if (v.GetProperty("result").GetString() == "unsafe")
            {
                Assert.True(r.IsUnsafe, label);
                Assert.Equal(v.GetProperty("unsafeEntry").GetString(), r.UnsafeEntry);
                continue;
            }
            Assert.False(r.IsUnsafe, label);
            Assert.Equal(v.StringMap("archives"), r.Archives);
            Assert.Equal(v.StringMap("sidecars"), r.Sidecars);
            Assert.Equal(v.StringOrNull("profilePath"), r.ProfilePath);
            foreach (var (raw, target) in v.StringMap("targets"))
            {
                Assert.True(r.Targets.TryGetValue(raw, out var actual) && actual == target, $"{label}: {raw}");
            }
        }
    }

    [Fact]
    public void Quote_MatchesGoldenVectors()
    {
        foreach (var v in Repo.Vectors("plugin-commands.json").GetProperty("quoting").EnumerateArray())
        {
            Assert.Equal(v.GetProperty("quoted").GetString(), GuestCommandLine.Quote(v.GetProperty("arg").GetString()!));
        }
    }

    [Fact]
    public void PluginCommand_MatchesGoldenVectors()
    {
        var cases = Repo.Vectors("plugin-commands.json").GetProperty("commands").EnumerateArray().ToList();
        Assert.NotEmpty(cases);
        foreach (var v in cases)
        {
            var verb = v.GetProperty("verb").GetString()!;
            var profile = v.GetProperty("profile").GetString()!;
            var operand = v.StringOrNull("operand");
            var label = $"{verb} {profile} {operand}";
            var ok = PluginCommand.TryBuild(verb, profile, operand, out var argv, out var error);
            Assert.True(v.GetProperty("valid").GetBoolean() == ok, $"{label}: {error}");
            if (ok)
            {
                Assert.Equal(v.GetProperty("argv").EnumerateArray().Select(a => a.GetString()!), argv);
                Assert.Equal(v.GetProperty("line").GetString(), GuestCommandLine.Join(argv));
            }
        }
    }
}
