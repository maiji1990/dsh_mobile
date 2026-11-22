using System.Text.RegularExpressions;

namespace DshMobile.Agent.Tests.Quality;

public sealed partial class SavedStateSecurityTests
{
    private static readonly string[] ForbiddenStateKeyFragments =
    [
        "confirm",
        "delete",
        "stop",
        "revoke",
        "password",
        "token",
        "apikey",
        "api_key",
        "secret",
        "credential",
        "cookie",
        "privatekey",
        "sshkey",
    ];

    [GeneratedRegex(@"\b(?:outState|savedInstanceState|bundle)\s*\.\s*Put[A-Za-z0-9_]*\s*\(\s*([^,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BundlePutCallRegex();

    [Fact]
    public void SavedInstanceState_NeverStoresConfirmationOrSecretKeys()
    {
        foreach (var file in RepoLocator.EnumerateSourceFiles("src/DshMobile.Android"))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in BundlePutCallRegex().Matches(text))
            {
                var keyExpr = m.Groups[1].Value;
                foreach (var forbidden in ForbiddenStateKeyFragments)
                {
                    Assert.DoesNotContain(forbidden, keyExpr, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }
}
