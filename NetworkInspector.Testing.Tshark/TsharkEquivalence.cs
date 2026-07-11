// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Tshark;

/// <summary>
/// Helpers for comparing field values produced by Network Inspector against values
/// reported by <c>tshark</c>. The comparison is intentionally semantic, not literal:
/// <list type="bullet">
///   <item>Field order is irrelevant — values are looked up by name.</item>
///   <item>Display-text formatting differences (hex casing, brackets, prefixes, leading
///         zeros, …) are normalised before comparison.</item>
///   <item>Numeric, IP and MAC address values are compared canonically.</item>
/// </list>
/// All <c>*TsharkTests</c> use this class instead of comparing strings directly so the
/// rules are applied consistently.
/// </summary>
public static class TsharkEquivalence
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="niValue"/> and
    /// <paramref name="tsharkValue"/> are semantically equivalent. <see langword="null"/>
    /// on either side means "field absent" and is treated as <see langword="false"/> —
    /// use <see cref="AreEquivalentOrAbsent"/> when missing-on-both should be considered
    /// equal.
    /// </summary>
    public static bool AreEquivalent(string? niValue, string? tsharkValue)
    {
        if (niValue is null || tsharkValue is null)
        {
            return false;
        }

        string a = _Normalize(niValue);
        string b = _Normalize(tsharkValue);
        if (a == b)
        {
            return true;
        }

        // Boolean equivalence — NI renders Bool fields as "True"/"False"
        // (via FieldValueData.TryFormatBool); tshark renders FT_BOOLEAN as "1"/"0".
        // Map across the two representations so that symmetric tshark tests can use
        // TsharkAssert.AssertEquivalentMany for bool-typed fields without workarounds.
        bool aIsTrue = string.Equals(a, "true", StringComparison.OrdinalIgnoreCase);
        bool aIsFalse = string.Equals(a, "false", StringComparison.OrdinalIgnoreCase);
        bool bIsTrue = string.Equals(b, "true", StringComparison.OrdinalIgnoreCase);
        bool bIsFalse = string.Equals(b, "false", StringComparison.OrdinalIgnoreCase);
        if ((aIsTrue && (b == "1" || bIsTrue)) || (aIsFalse && (b == "0" || bIsFalse)) ||
            (bIsTrue && (a == "1" || aIsTrue)) || (bIsFalse && (a == "0" || aIsFalse)))
        {
            return true;
        }

        // Integer equivalence — both parse as the same numeric value.
        if (_TryParseInteger(a, out long ia) && _TryParseInteger(b, out long ib))
        {
            return ia == ib;
        }

        // IP-address equivalence — canonicalize and compare.
        if (_TryParseIpAddress(a, out IPAddress? ipA) && _TryParseIpAddress(b, out IPAddress? ipB))
        {
            return ipA!.Equals(ipB);
        }

        // MAC-address equivalence — strip separators and compare hex digits.
        string macA = _StripMacSeparators(a);
        string macB = _StripMacSeparators(b);
        if (macA.Length == 12 && macB.Length == 12 && _IsHex(macA) && _IsHex(macB))
        {
            return string.Equals(macA, macB, StringComparison.OrdinalIgnoreCase);
        }

        // Generic byte-sequence equivalence — for fields like eth.padding,
        // tshark renders bytes as a contiguous hex blob ("00000000…") while NI
        // formats them with single-space separators ("00 00 00 00…"). Both
        // representations are semantically equal once whitespace and common
        // separators are stripped.
        string hexA = _StripHexSeparators(a);
        string hexB = _StripHexSeparators(b);
        if (hexA.Length > 0 && hexA.Length == hexB.Length && (hexA.Length & 1) == 0
            && _IsHex(hexA) && _IsHex(hexB))
        {
            return string.Equals(hexA, hexB, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Same as <see cref="AreEquivalent"/> but treats <see langword="null"/> on both
    /// sides as equal.
    /// </summary>
    public static bool AreEquivalentOrAbsent(string? niValue, string? tsharkValue)
    {
        if (niValue is null && tsharkValue is null)
        {
            return true;
        }
        return AreEquivalent(niValue, tsharkValue);
    }

    /// <summary>
    /// Multi-set comparison of two unordered collections. Two collections are equivalent
    /// when they contain the same multiset of values according to <see cref="AreEquivalent"/>.
    /// Order does not matter on either side.
    /// </summary>
    public static bool AreEquivalentSet(IEnumerable<string?> ni, IEnumerable<string?> tshark)
    {
        List<string?> niList = [.. ni];
        List<string?> tsList = [.. tshark];
        if (niList.Count != tsList.Count)
        {
            return false;
        }

        bool[] used = new bool[tsList.Count];
        foreach (string? n in niList)
        {
            int match = -1;
            for (int i = 0; i < tsList.Count; i++)
            {
                if (used[i])
                {
                    continue;
                }
                if (AreEquivalentOrAbsent(n, tsList[i]))
                {
                    match = i;
                    break;
                }
            }
            if (match < 0)
            {
                return false;
            }
            used[match] = true;
        }
        return true;
    }

    /// <summary>
    /// Builds a human-readable diagnostic message for failed comparisons. Used in TUnit
    /// <c>.Because(...)</c> clauses across the test projects.
    /// </summary>
    public static string Describe(string fieldName, string? niValue, string? tsharkValue)
        => $"Field '{fieldName}' — NI='{niValue ?? "<absent>"}' vs tshark='{tsharkValue ?? "<absent>"}'";

    #region Normalisation helpers

    /// <summary>Trims whitespace, strips outer brackets, lower-cases hex prefix.</summary>
    private static string _Normalize(string value)
    {
        string s = value.Trim();
        // Strip surrounding parentheses or square brackets sometimes used in display.
        while (s.Length >= 2 && (s[0] == '(' || s[0] == '[') && (s[^1] == ')' || s[^1] == ']'))
        {
            s = s[1..^1].Trim();
        }
        if (s.StartsWith("0X", StringComparison.Ordinal))
        {
            s = "0x" + s[2..];
        }
        return s;
    }

    private static bool _TryParseInteger(string s, out long value)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool _TryParseIpAddress(string s, out IPAddress? addr)
        => IPAddress.TryParse(s, out addr);

    private static string _StripMacSeparators(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        int len = 0;
        foreach (char c in s)
        {
            if (c is ':' or '-' or '.')
            {
                continue;
            }
            buf[len++] = c;
        }
        return new string(buf[..len]);
    }

    /// <summary>
    /// Strips whitespace and common byte-grouping separators from a hex blob so that
    /// space-grouped ("00 00 00 00") and contiguous ("00000000") representations
    /// compare equal.
    /// </summary>
    private static string _StripHexSeparators(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        int len = 0;
        foreach (char c in s)
        {
            if (c is ' ' or '\t' or ':' or '-' or '.')
            {
                continue;
            }
            buf[len++] = c;
        }
        return new string(buf[..len]);
    }

    private static bool _IsHex(string s)
    {
        foreach (char c in s)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok)
            {
                return false;
            }
        }
        return s.Length > 0;
    }

    #endregion
}
