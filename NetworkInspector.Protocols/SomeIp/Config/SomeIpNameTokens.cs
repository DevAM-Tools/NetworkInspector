// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp.Config;

/// <summary>
/// The one deterministic rule that turns configured names into the identifiers of registered field names. Configured names
/// carry spaces, dashes and mixed case; registered names must be identifiers the host accepts and a filter can type. With one
/// rule an author can predict the filter string of every value without opening the tool.
/// <para>
/// The registered name of a payload value is
/// <c>someip.payload.&lt;service&gt;.v&lt;major&gt;.&lt;member&gt;[.in|.out].&lt;path...&gt;</c>. Each segment is a token:
/// ASCII letters are lower-cased, ASCII letters, digits and underscores stay, every other character becomes an underscore
/// (one per UTF-16 code unit, so the result is stable and needs no Unicode tables), and a leading digit gets an underscore in
/// front. The direction segment appears for members that have a request and a response (methods, getters, setters); the path
/// lists structure members, union alternatives and bitfield bits by name and array elements by the segment
/// <see cref="ArrayElement"/>.
/// </para>
/// <para><b>Thread-safety:</b> stateless.</para>
/// </summary>
internal static class SomeIpNameTokens
{
    /// <summary>The prefix of every registered payload field name.</summary>
    internal const string Prefix = "someip.payload";

    /// <summary>The path segment of an array element.</summary>
    internal const string ArrayElement = "item";

    /// <summary>The token used when a configured name is empty, so a registered name never contains an empty segment.</summary>
    private const string _EmptyToken = "_";

    /// <summary>Applies the token rule to a configured name.</summary>
    /// <param name="configured">The configured name; may be empty.</param>
    /// <returns>A non-empty identifier segment.</returns>
    internal static string Token(string configured)
    {
        if (configured.Length == 0)
        {
            return _EmptyToken;
        }

        // string.Create sizes the result exactly once: the result is one character per input character plus an optional
        // leading underscore, so no builder and no intermediate string is needed.
        bool leadingDigit = configured[0] is >= '0' and <= '9';
        int length = configured.Length;
        if (leadingDigit)
        {
            length++;
        }

        return string.Create(length, (configured, leadingDigit), static (span, state) =>
        {
            int offset = 0;
            if (state.leadingDigit)
            {
                span[offset++] = '_';
            }

            foreach (char c in state.configured)
            {
                span[offset++] = _Map(c);
            }
        });
    }

    /// <summary>The service segment, <c>{token}.v{major}</c>.</summary>
    /// <param name="serviceName">The configured service name.</param>
    /// <param name="major">The interface major version.</param>
    internal static string Service(string serviceName, byte major) =>
        string.Create(CultureInfo.InvariantCulture, $"{Token(serviceName)}.v{major}");

    /// <summary>The member path below a service path.</summary>
    /// <param name="servicePath">The result of <see cref="Service"/>.</param>
    /// <param name="memberToken">The member token (already a token).</param>
    /// <param name="direction">"in", "out", or null for members without a direction segment.</param>
    internal static string Member(string servicePath, string memberToken, string? direction) =>
        direction is null
            ? string.Concat(servicePath, ".", memberToken)
            : string.Concat(servicePath, ".", memberToken, ".", direction);

    /// <summary>Appends a segment to a path.</summary>
    /// <param name="parentPath">The parent path.</param>
    /// <param name="childToken">The child token (already a token).</param>
    internal static string Child(string parentPath, string childToken) => string.Concat(parentPath, ".", childToken);

    /// <summary>The default token of a TLV member without a name.</summary>
    /// <param name="id">The TLV id.</param>
    internal static string TlvDefault(uint id) => string.Create(CultureInfo.InvariantCulture, $"member_{id}");

    private static char _Map(char c)
    {
        if (c is >= 'A' and <= 'Z')
        {
            return (char)(c + ('a' - 'A'));
        }

        return c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'
            ? c
            : '_';
    }
}
