using System.Text.RegularExpressions;

namespace DshMobile.Agent.Tests.Quality;

public sealed partial class CancellationAndFailureGateTests
{
    [GeneratedRegex(@"catch\s*\(\s*Exception(?:\s+[A-Za-z_][A-Za-z0-9_]*)?\s*\)(?!\s*when\s*\([^)]*OperationCanceledException)", RegexOptions.Multiline)]
    private static partial Regex BareCatchExceptionRegex();

    [GeneratedRegex(@"\b(?:ex|err|error|exception)\s*\.\s*Message\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExceptionMessageAccessRegex();

    [Fact]
    public void DshMobileAgent_NeverSwallowsOperationCanceledExceptionInCatchException()
    {
        foreach (var file in RepoLocator.EnumerateSourceFiles("src/DshMobile.Agent"))
        {
            var text = File.ReadAllText(file);
            var matches = BareCatchExceptionRegex().Matches(text);
            foreach (Match m in matches)
            {
                var tail = text.Substring(m.Index, Math.Min(200, text.Length - m.Index));
                Assert.True(tail.Contains("OperationCanceledException", StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} has a catch (Exception) without an OperationCanceledException guard.");
            }
        }
    }

    [Fact]
    public void DshMobileAgentPresentation_NeverExposesRawExceptionMessage()
    {
        foreach (var file in RepoLocator.EnumerateSourceFiles("src/DshMobile.Agent/Presentation"))
        {
            var text = File.ReadAllText(file);
            Assert.False(ExceptionMessageAccessRegex().IsMatch(text),
                $"Presentation file {Path.GetFileName(file)} must not expose raw exception.Message to UI state.");
        }
    }
}
