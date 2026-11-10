// Ported from DSHBox v1.5.1 (7c9d33c) common/src/main/java/com/dshbox/app/common/Versions.kt.
// Copyright (C) DSHBox contributors, GPL-3.0-or-later; C# translation part of DSH Mobile (GPL-3.0).
// Parity: tests/TestVectors/versions.json runs against both implementations (docs/UPSTREAMS.md, PR-02).

namespace DshMobile.Runtime;

/// <summary>
/// Version arbitration for runtime / DSH layers: handles <c>0.1.0-rc.6</c>, <c>v0.1.2</c>, <c>0.1.1-rc.2-patched</c>.
/// Leading <c>v</c> is ignored; numeric part compared per segment (non-numeric segments dropped, missing = 0); a
/// release is newer than any prerelease of the same number; prerelease segments compare numerically when both are
/// integers, otherwise ordinally; with equal shared segments, more segments wins.
/// </summary>
public static class Versions
{
    public static int Compare(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var (aNum, aPre) = SplitPrerelease(Clean(a));
        var (bNum, bPre) = SplitPrerelease(Clean(b));
        var pa = NumericSegments(aNum);
        var pb = NumericSegments(bNum);
        for (var i = 0; i < Math.Max(pa.Count, pb.Count); i++)
        {
            var x = i < pa.Count ? pa[i] : 0;
            var y = i < pb.Count ? pb[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }
        if (string.Equals(aPre, bPre, StringComparison.Ordinal))
        {
            return 0;
        }
        if (aPre.Length == 0)
        {
            return 1;
        }
        if (bPre.Length == 0)
        {
            return -1;
        }
        return ComparePrerelease(aPre, bPre);
    }

    /// <summary>Whether <paramref name="a"/> is strictly newer than <paramref name="b"/>.</summary>
    public static bool IsNewer(string a, string b) => Compare(a, b) > 0;

    // Kotlin trimStart('v') strips every leading 'v'; trim() strips whitespace.
    private static string Clean(string s) => s.Trim().TrimStart('v');

    private static (string Num, string Pre) SplitPrerelease(string s)
    {
        var idx = s.IndexOf('-', StringComparison.Ordinal);
        return idx < 0 ? (s, "") : (s[..idx], s[(idx + 1)..]);
    }

    private static List<int> NumericSegments(string s)
    {
        var list = new List<int>();
        foreach (var seg in s.Split('.'))
        {
            if (TryParseKotlinInt(seg, out var n))
            {
                list.Add(n);
            }
        }
        return list;
    }

    private static int ComparePrerelease(string a, string b)
    {
        var sa = a.Split('.');
        var sb = b.Split('.');
        var shared = Math.Min(sa.Length, sb.Length);
        for (var i = 0; i < shared; i++)
        {
            var c = TryParseKotlinInt(sa[i], out var nx) && TryParseKotlinInt(sb[i], out var ny)
                ? nx.CompareTo(ny)
                : string.CompareOrdinal(sa[i], sb[i]);
            if (c != 0)
            {
                return Math.Sign(c);
            }
        }
        return sa.Length.CompareTo(sb.Length);
    }

    /// <summary>Kotlin <c>String.toIntOrNull()</c>: optional sign, ASCII digits only, 32-bit range.</summary>
    private static bool TryParseKotlinInt(string s, out int value)
    {
        value = 0;
        if (s.Length == 0)
        {
            return false;
        }
        var start = s[0] is '+' or '-' ? 1 : 0;
        if (start == s.Length)
        {
            return false;
        }
        for (var i = start; i < s.Length; i++)
        {
            if (s[i] is < '0' or > '9')
            {
                return false;
            }
        }
        return int.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
