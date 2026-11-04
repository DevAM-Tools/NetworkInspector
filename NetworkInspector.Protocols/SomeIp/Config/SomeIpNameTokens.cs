// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp.Config;

/// <summary>
/// The naming rules of the SOME/IP description: every name of the description is used unchanged as one segment of a filter name
/// (<c>[prefix.]Service.v2.Method[.req|.resp].path</c>). Nothing is rewritten: a name that breaks the segment rule is refused by the
/// compiler, because a rewritten name is a name the author never wrote and cannot find. Index group names are internal and always
/// start with <see cref="GroupPrefix"/>, so they never meet the groups of other components whatever the field prefix is.
/// <para><b>Thread-safety:</b> stateless.</para>
/// </summary>
internal static class SomeIpNameTokens
{
    /// <summary>The prefix of every index group name of the description (internal; never shown to the user).</summary>
    internal const string GroupPrefix = "someip.payload";

    /// <summary>The path segment of an array element.</summary>
    internal const string ArrayElement = "item";

    /// <summary>The direction segment of a request parameter list.</summary>
    internal const string Request = "req";

    /// <summary>The direction segment of a response parameter list.</summary>
    internal const string Response = "resp";

    /// <summary>A segment: a host name without a dot (a dot would add a level the description does not have).</summary>
    /// <param name="name">The configured or default name; may be null.</param>
    internal static bool IsValidSegment(string? name) =>
        !string.IsNullOrEmpty(name) && name.AsSpan().IndexOf('.') < 0 && NameValidation.IsValidName(name);

    /// <summary>A prefix: one or more segments joined by dots, which is exactly a host name.</summary>
    /// <param name="prefix">The configured prefix.</param>
    internal static bool IsValidPrefix(string prefix) => NameValidation.IsValidName(prefix);

    /// <summary>The service segment, <c>{name}.v{major}</c>.</summary>
    /// <param name="name">The configured service name (already a valid segment).</param>
    /// <param name="major">The interface major version.</param>
    internal static string Service(string name, byte major) => string.Create(CultureInfo.InvariantCulture, $"{name}.v{major}");

    /// <summary>Puts the optional prefix in front of a path.</summary>
    /// <param name="prefix">The validated prefix; empty means none.</param>
    /// <param name="path">The path.</param>
    internal static string Join(string prefix, string path) =>
        prefix.Length == 0
            ? path
            : string.Concat(prefix, ".", path);

    /// <summary>Appends a segment to a path.</summary>
    /// <param name="parentPath">The parent path.</param>
    /// <param name="segment">The child segment (already a valid segment).</param>
    internal static string Child(string parentPath, string segment) => string.Concat(parentPath, ".", segment);

    /// <summary>The default segment of a TLV member without a name.</summary>
    /// <param name="id">The TLV id.</param>
    internal static string TlvDefault(uint id) => string.Create(CultureInfo.InvariantCulture, $"member_{id}");
}
