using System.Text.Json;

namespace DshMobile.Runtime.Tests;

/// <summary>Locates the repository root (global.json) and the shared golden vectors in tests/TestVectors.</summary>
internal static class Repo
{
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            return dir.FullName;
        }
    }

    public static string PathOf(params string[] parts) => Path.Combine([Root, .. parts]);

    public static JsonElement Vectors(string file) =>
        JsonDocument.Parse(File.ReadAllText(PathOf("tests", "TestVectors", file))).RootElement.Clone();

    public static Dictionary<string, string> StringMap(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var o) && o.ValueKind == JsonValueKind.Object
            ? o.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!)
            : [];

    public static string? StringOrNull(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
