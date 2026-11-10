// Ported from DSHBox v1.5.1 (7c9d33c) sandbox-manager/.../RuntimeProfile.kt (GPL-3.0-or-later).

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshMobile.Runtime.Bundle;

/// <summary>Parsed <c>runtime-profile.json</c>: how runtime layers are assembled and validated. DSH is not a layer.</summary>
public sealed record RuntimeProfile(
    string Version,
    string Arch,
    string Compression,
    int ZstdLevel,
    IReadOnlyList<RuntimeLayer> Layers,
    IReadOnlyList<string> Assembly)
{
    public RuntimeLayer? Layer(string name) => Layers.FirstOrDefault(l => l.Name == name);

    /// <summary>Same leniency as upstream: null when <c>bundle</c> or <c>layers</c> is missing or JSON is invalid.</summary>
    public static RuntimeProfile? Parse(string json)
    {
        try
        {
            var doc = JsonSerializer.Deserialize(json, RuntimeProfileJsonContext.Default.RuntimeProfileDocument);
            if (doc?.Bundle is null || doc.Layers is null)
            {
                return null;
            }
            var layers = doc.Layers.Where(l => l is not null).Select(l => new RuntimeLayer(
                l!.Name ?? "", l.Version ?? "", l.Compression ?? "", l.Sha256 ?? "", l.SizeBytes ?? 0,
                l.EnvFile ?? "", l.File ?? "", l.Deps ?? [])).ToList();
            return new RuntimeProfile(
                doc.Bundle.Version ?? "",
                doc.Bundle.Arch ?? "",
                doc.Bundle.Compression ?? "",
                doc.Bundle.ZstdLevel ?? 19,
                layers,
                doc.Assembly ?? layers.Select(l => l.Name).ToList());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record RuntimeLayer(
    string Name,
    string Version,
    string Compression,
    string Sha256,
    long SizeBytes,
    string EnvFile,
    string File,
    IReadOnlyList<string> Deps)
{
    public bool IsZstd => Compression.Equals("zstd", StringComparison.OrdinalIgnoreCase);

    public bool IsGzip => Compression.Equals("gzip", StringComparison.OrdinalIgnoreCase);
}

internal sealed record RuntimeProfileDocument(
    [property: JsonPropertyName("bundle")] RuntimeProfileBundle? Bundle,
    [property: JsonPropertyName("layers")] List<RuntimeProfileLayer?>? Layers,
    [property: JsonPropertyName("assembly")] List<string>? Assembly);

internal sealed record RuntimeProfileBundle(
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("arch")] string? Arch,
    [property: JsonPropertyName("compression")] string? Compression,
    [property: JsonPropertyName("zstd_level")] int? ZstdLevel);

internal sealed record RuntimeProfileLayer(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("compression")] string? Compression,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("size_bytes")] long? SizeBytes,
    [property: JsonPropertyName("env_file")] string? EnvFile,
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("deps")] List<string>? Deps);

[JsonSourceGenerationOptions(AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(RuntimeProfileDocument))]
internal sealed partial class RuntimeProfileJsonContext : JsonSerializerContext;
