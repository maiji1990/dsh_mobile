using System.Text.RegularExpressions;

namespace DshMobile.Agent.Tests.Quality;

public sealed partial class LoggingPrivacyTests
{
    private static readonly string[] ForbiddenLoggedIdentifiers =
    [
        "apiKey",
        "api_key",
        "launchToken",
        "password",
        "privateKey",
        "authorizationHeader",
        "cookieHeader",
        "rawPrompt",
        "promptText",
    ];

    [GeneratedRegex(@"(?:Log\.(?:Info|Warn|Error|Debug|Verbose)|Console\.Write(?:Line)?|Debug\.Write(?:Line)?|_log\.Note)\s*\(([^;\n]+)", RegexOptions.Multiline)]
    private static partial Regex LogInvocationRegex();

    [Fact]
    public void AgentAndAndroidLogging_NeverLogsSensitiveIdentifiers()
    {
        var dirs = new[] { "src/DshMobile.Agent", "src/DshMobile.Android" };
        foreach (var dir in dirs)
        {
            foreach (var file in RepoLocator.EnumerateSourceFiles(dir))
            {
                var text = File.ReadAllText(file);
                foreach (Match m in LogInvocationRegex().Matches(text))
                {
                    var args = m.Groups[1].Value;
                    foreach (var forbidden in ForbiddenLoggedIdentifiers)
                    {
                        Assert.DoesNotContain(forbidden, args, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
        }
    }
}
