// `dsh plugin` through the C# PRoot launcher. Mirrors the legacy RuntimeController.runPluginCommand / runGuestScript
// (PR-00 argv quoting) and moves the TASK-009 list parser out of PluginsActivity so screen and shadow share it.

using System.Text.Json;
using DshMobile.Runtime.Hosting;

namespace DshMobile.Runtime.Guest;

public sealed record GuestPluginResult(bool Ok, int? ExitCode, string Output, string? Error);

public static class GuestPlugins
{
    /// <summary>Same PATH the legacy runGuestScript exports (DSH's own role adds these: <c>dsh plugin</c> needs pnpm).</summary>
    public const string PathExport = "export PATH=/opt/dshbox:/root/projects/.dsh/dshbox/bin:$PATH";

    public static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan ChangeTimeout = TimeSpan.FromMinutes(15);

    /// <summary>The guest shell line for validated argv (every token quoted; nothing from the caller is raw).</summary>
    public static string ShellLine(IReadOnlyList<string> argv) =>
        $"{PathExport}; cd {RuntimeConstants.SandboxWorkspace} && {GuestCommandLine.Quote(RuntimeConstants.GuestDshWrapper)} {GuestCommandLine.Join(argv)}";

    /// <summary>Runs <c>dsh plugin &lt;verb&gt;</c>; a bad verb / profile / operand is BAD_REQUEST and nothing runs.</summary>
    public static async Task<GuestPluginResult> RunAsync(ProotGuestProcess exec, string verb, string profile, string? operand, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(exec);
        if (!PluginCommand.TryBuild(verb, profile, operand, out var argv, out var error))
        {
            return new GuestPluginResult(false, null, "", "BAD_REQUEST: " + error);
        }
        var r = await exec.RunShellAsync(ShellLine(argv),
            new GuestExecutionOptions(Timeout: verb == "list" ? ListTimeout : ChangeTimeout, MaxOutputLines: 2000), ct).ConfigureAwait(false);
        // Output reaches the screen and logs: redact like the legacy guest-command line handler does.
        var output = string.Join('\n', r.Output.Split('\n').Select(LogRedactor.Redact));
        return new GuestPluginResult(r.Ok, r.ExitCode, output, r.Error);
    }

    /// <summary>
    /// Plugins from <c>dsh plugin list --json --depth 0</c> (pnpm list JSON, possibly after other output): the
    /// dependency sections of every project, sorted by name. Empty for unparsable output.
    /// </summary>
    public static IReadOnlyList<(string Name, string Version)> ParseList(string output) =>
        TryParseList(output, out var list) ? list : [];

    /// <summary>Like <see cref="ParseList"/>, but tells an empty list (<c>[]</c>) apart from unparsable output.</summary>
    public static bool TryParseList(string output, out IReadOnlyList<(string Name, string Version)> list)
    {
        ArgumentNullException.ThrowIfNull(output);
        list = [];
        var start = output.IndexOf('[', StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(output[start..]);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }
            var result = new List<(string, string)>();
            foreach (var project in doc.RootElement.EnumerateArray())
            {
                foreach (var section in new[] { "dependencies", "devDependencies", "optionalDependencies" })
                {
                    if (project.TryGetProperty(section, out var deps) && deps.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var d in deps.EnumerateObject())
                        {
                            var version = d.Value.ValueKind == JsonValueKind.Object && d.Value.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                            result.Add((d.Name, version));
                        }
                    }
                }
            }
            list = result.OrderBy(p => p.Item1, StringComparer.Ordinal).ToList();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
