using System.Text.RegularExpressions;

namespace DshMobile.Agent.Domain;

/// <summary>
/// 严格的 SemVer 2.0 版本比较器（供插件“检查更新”使用）：
/// 忽略前导 <c>v</c>/<c>V</c> 与构建元数据，按 <c>major.minor.patch</c> 数值比较；
/// 同一主次修订号下正式版高于预发布版（如 <c>1.0.0 &gt; 1.0.0-rc.1</c>），
/// 预发布标识符按 SemVer 规则比较（数字标识符按数值、字母标识符按字典序，数字低于字母）。
/// 任何非法版本都安全返回“不可比较”，绝不误判出倒退更新。
/// </summary>
public static partial class PluginVersionComparer
{
    private const int MaxVersionLength = 128;
    private const int MaxNumericIdentifierLength = 18;

    [GeneratedRegex(
        @"^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<prerelease>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+(?<build>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SemanticVersionRegex();

    /// <summary>判断给定字符串是否为可比较的合法 SemVer（可带前导 <c>v</c>/<c>V</c>）。</summary>
    public static bool IsValidVersion(string? version) => TryParse(version, out _);

    /// <summary>
    /// 仅当已安装版本与最新版本都是合法 SemVer 且最新版本严格更高时返回 <see langword="true"/>；
    /// 任一侧非法、相等或最新版本更低都返回 <see langword="false"/>（不会把降级误判为更新）。
    /// </summary>
    public static bool IsUpdateAvailable(string? installedVersion, string? latestVersion) =>
        TryCompare(latestVersion, installedVersion, out var comparison) && comparison > 0;

    /// <summary>
    /// 比较两个 SemVer：左侧更高返回正数，相等返回 0，左侧更低返回负数。
    /// 任一侧非法时返回 <see langword="false"/> 且 <paramref name="comparison"/> 为 0。
    /// </summary>
    public static bool TryCompare(string? leftVersion, string? rightVersion, out int comparison)
    {
        comparison = 0;
        if (!TryParse(leftVersion, out var left) || !TryParse(rightVersion, out var right))
        {
            return false;
        }
        comparison = CompareCore(left, right);
        return true;
    }

    private readonly record struct ParsedVersion(
        string Major,
        string Minor,
        string Patch,
        string[] PrereleaseIdentifiers);

    private static bool TryParse(string? rawVersion, out ParsedVersion parsedVersion)
    {
        parsedVersion = default;
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return false;
        }

        var candidate = rawVersion.Trim();
        if (candidate.Length > MaxVersionLength)
        {
            return false;
        }

        // 仅剥离紧邻数字的前导 v/V；其它形式（如 "version1.0.0"）一律交给正则拒绝。
        if (candidate.Length > 1 && (candidate[0] == 'v' || candidate[0] == 'V') && char.IsAsciiDigit(candidate[1]))
        {
            candidate = candidate[1..];
        }

        var match = SemanticVersionRegex().Match(candidate);
        if (!match.Success || match.Index != 0 || match.Length != candidate.Length)
        {
            return false;
        }

        var prereleaseGroup = match.Groups["prerelease"];
        var identifiers = prereleaseGroup.Success
            ? prereleaseGroup.Value.Split('.')
            : [];
        foreach (var identifier in identifiers)
        {
            // SemVer 2.0：纯数字的预发布标识符不得有前导零，且长度受限以避免数值溢出。
            if (IsNumericIdentifier(identifier))
            {
                if (identifier.Length > 1 && identifier[0] == '0')
                {
                    return false;
                }
                if (identifier.Length > MaxNumericIdentifierLength)
                {
                    return false;
                }
            }
        }

        parsedVersion = new ParsedVersion(
            match.Groups["major"].Value,
            match.Groups["minor"].Value,
            match.Groups["patch"].Value,
            identifiers);
        return true;
    }

    private static int CompareCore(ParsedVersion left, ParsedVersion right)
    {
        var comparison = CompareNumericIdentifier(left.Major, right.Major);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareNumericIdentifier(left.Minor, right.Minor);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = CompareNumericIdentifier(left.Patch, right.Patch);
        if (comparison != 0)
        {
            return comparison;
        }

        // 同一主次修订号：正式版高于任何预发布版。
        if (left.PrereleaseIdentifiers.Length == 0 && right.PrereleaseIdentifiers.Length == 0)
        {
            return 0;
        }
        if (left.PrereleaseIdentifiers.Length == 0)
        {
            return 1;
        }
        if (right.PrereleaseIdentifiers.Length == 0)
        {
            return -1;
        }

        var sharedLength = Math.Min(left.PrereleaseIdentifiers.Length, right.PrereleaseIdentifiers.Length);
        for (var index = 0; index < sharedLength; index++)
        {
            comparison = ComparePrereleaseIdentifier(
                left.PrereleaseIdentifiers[index],
                right.PrereleaseIdentifiers[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }
        return left.PrereleaseIdentifiers.Length.CompareTo(right.PrereleaseIdentifiers.Length);
    }

    /// <summary>比较纯数字标识符：严格 SemVer 下无前导零，长度即为数量级，再按字典序兜底。</summary>
    private static int CompareNumericIdentifier(string left, string right)
    {
        var trimmedLeft = left.TrimStart('0');
        var trimmedRight = right.TrimStart('0');
        if (trimmedLeft.Length == 0 && trimmedRight.Length == 0)
        {
            return 0;
        }
        if (trimmedLeft.Length != trimmedRight.Length)
        {
            return trimmedLeft.Length.CompareTo(trimmedRight.Length);
        }
        return string.CompareOrdinal(trimmedLeft, trimmedRight);
    }

    /// <summary>SemVer 预发布标识符比较：数字低于字母；同为数字按数值，同为字母按 ASCII 字典序。</summary>
    private static int ComparePrereleaseIdentifier(string left, string right)
    {
        var leftIsNumeric = IsNumericIdentifier(left);
        var rightIsNumeric = IsNumericIdentifier(right);
        if (leftIsNumeric && rightIsNumeric)
        {
            return CompareNumericIdentifier(left, right);
        }
        if (leftIsNumeric)
        {
            return -1;
        }
        if (rightIsNumeric)
        {
            return 1;
        }
        return string.CompareOrdinal(left, right);
    }

    private static bool IsNumericIdentifier(string identifier) =>
        identifier.Length > 0 && identifier.All(char.IsAsciiDigit);
}
