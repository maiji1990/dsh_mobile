using System.Text;
using System.Text.Json;
using DshMobile.Runtime.Automation;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// PR-07: the C# ui_* registration over tests/TestVectors/ui-tools-registration.json (the legacy Kotlin
/// LegacyUiToolsRegistrationParityTest runs the same cases), and the accessibility-setting parser over
/// accessibility-enabled.json.
/// </summary>
public sealed class UiToolsRegistrationParityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dshm-uitools-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static readonly JsonElement Vectors = Repo.Vectors("ui-tools-registration.json");

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var c in Vectors.GetProperty("cases").EnumerateArray())
        {
            data.Add(c.GetProperty("name").GetString()!);
        }
        return data;
    }

    private static JsonElement Case(string name) =>
        Vectors.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    [Fact]
    public void RenderAndPath_MatchTheVectors()
    {
        Assert.Equal(Vectors.GetProperty("render").GetString(), UiToolsRegistration.Render());
        Assert.Equal(UiToolsRegistration.OverlayRelativePath, Vectors.GetProperty("overlayRelativePath").GetString());
        Assert.True(Vectors.GetProperty("cases").GetArrayLength() >= 10);
        // One top-level sequence item: a second top-level node would make DSH drop the whole layer.
        Assert.Single(UiToolsRegistration.Render().Split('\n'), l => l.StartsWith("- ", StringComparison.Ordinal));
    }

    private string Setup(string name, JsonElement initial)
    {
        var userData = Path.Combine(_root, Guid.NewGuid().ToString("N"), "user-data");
        Directory.CreateDirectory(userData);
        var path = UiToolsRegistration.OverlayFile(userData);
        if (initial.ValueKind == JsonValueKind.String)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var text = initial.GetString()!;
            if (text == "@dir")
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text == "@render" ? UiToolsRegistration.Render() : text));
            }
        }
        Assert.True(Directory.Exists(userData), name);
        return userData;
    }

    private static void AssertAfter(string name, string step, string userData, JsonElement initial, JsonElement after)
    {
        var path = UiToolsRegistration.OverlayFile(userData);
        var expected = after.ValueKind == JsonValueKind.Null ? null : after.GetString();
        if (expected == "@initial")
        {
            expected = initial.ValueKind == JsonValueKind.Null ? null : initial.GetString();
        }
        switch (expected)
        {
            case null:
                Assert.False(File.Exists(path) || Directory.Exists(path), $"{name} / {step}: expected absent");
                break;
            case "@dir":
                Assert.True(Directory.Exists(path), $"{name} / {step}: expected the directory to stay");
                break;
            default:
                Assert.True(File.Exists(path), $"{name} / {step}: expected a file");
                var want = expected == "@render" ? UiToolsRegistration.Render() : expected;
                Assert.Equal(want, Encoding.UTF8.GetString(File.ReadAllBytes(path)));
                break;
        }
        Assert.False(File.Exists(path + ".tmp"), $"{name} / {step}: temp file left behind");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case_MatchesTheVector(string name)
    {
        var c = Case(name);
        var initial = c.GetProperty("initial");

        var userData = Setup(name, initial);
        Assert.Equal(c.GetProperty("isRegistered").GetBoolean(), UiToolsRegistration.IsRegistered(userData));
        Assert.Equal(c.GetProperty("hasConflict").GetBoolean(), UiToolsRegistration.HasConflict(userData));

        var reg = c.GetProperty("register");
        Assert.Equal(reg.GetProperty("result").GetBoolean(), UiToolsRegistration.Register(userData));
        AssertAfter(name, "register", userData, initial, reg.GetProperty("after"));

        userData = Setup(name, initial);
        var unreg = c.GetProperty("unregister");
        Assert.Equal(unreg.GetProperty("result").GetBoolean(), UiToolsRegistration.Unregister(userData));
        AssertAfter(name, "unregister", userData, initial, unreg.GetProperty("after"));
    }

    [Fact]
    public void AccessibilitySetting_MatchesTheVectors()
    {
        var v = Repo.Vectors("accessibility-enabled.json");
        var package = v.GetProperty("package").GetString()!;
        var service = v.GetProperty("service").GetString()!;
        Assert.Equal(AccessibilitySetting.RelayServiceClass, service);
        var cases = v.GetProperty("cases").EnumerateArray().ToList();
        Assert.True(cases.Count >= 10);
        foreach (var c in cases)
        {
            var setting = c.GetProperty("setting").ValueKind == JsonValueKind.Null ? null : c.GetProperty("setting").GetString();
            Assert.True(c.GetProperty("expected").GetBoolean() == AccessibilitySetting.IsEnabled(setting, package, service),
                c.GetProperty("name").GetString());
        }
    }

    [Fact]
    public void Unflatten_FollowsComponentNameRules()
    {
        Assert.Equal(("a.b", "a.b.C"), AccessibilitySetting.Unflatten("a.b/.C"));
        Assert.Equal(("a.b", "x.Y"), AccessibilitySetting.Unflatten("a.b/x.Y"));
        Assert.Equal(("", "x.Y"), AccessibilitySetting.Unflatten("/x.Y"));
        Assert.Equal(("a", "b/c"), AccessibilitySetting.Unflatten("a/b/c")); // first '/' only
        Assert.Null(AccessibilitySetting.Unflatten("a.b"));
        Assert.Null(AccessibilitySetting.Unflatten("a.b/"));
    }
}
