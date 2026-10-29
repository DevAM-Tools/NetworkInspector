// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Values;

/// <summary>
/// Letter case of the hexadecimal digits that <see cref="MacAddress"/>, <see cref="Eui64"/>, <see cref="Uuid"/> and
/// <see cref="IPv6Address"/> write.
/// <para>
/// <b>Why a choice:</b> both spellings are in use. Exports, filters and reports that were written against the upper-case
/// text must keep matching, so <see cref="Upper"/> is the default everywhere; tools and people that follow the
/// recommendation for IPv6 text (RFC 5952 section 4.3 asks for lower case) pick <see cref="Lower"/>. The choice is
/// made per call, either with <c>Format(HexCase.Lower)</c> or with the format specifier <c>x</c> (<c>X</c> and the empty
/// format give upper case), so interpolation (<c>$"{address:x}"</c>) and <c>ToString("x")</c> work without a helper.
/// </para>
/// <para>Thread safety: an enumeration, safe for concurrent use.</para>
/// </summary>
public enum HexCase
{
    /// <summary>Digits A to F in upper case ("00:1A:2B:3C:4D:5E"). The default.</summary>
    Upper = 0,

    /// <summary>Digits a to f in lower case ("00:1a:2b:3c:4d:5e").</summary>
    Lower = 1,
}
