// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits;

/// <summary>
/// The seam between a <see cref="LimitDefinition"/> and the unit of its values: how a text becomes a whole number of the base
/// unit and back. The core knows nothing else about units. Implementations must be immutable and thread-safe.
/// </summary>
/// <remarks>
/// Most applications do not implement this interface: the utility part of this package (the <c>Utility</c> namespace) builds a
/// unit from a few words (<c>TableUnit</c>) and ships the common ones (<c>LimitUnits</c>). Implement it directly for a unit
/// that is not table-shaped (a date, a percentage with a fraction).
/// <para>Rejected: a closed enumeration of units (every domain would need a change of the library) and a global registry
/// (global state that two components of one process would fill in conflicting ways).</para>
/// <para>Sync: <see cref="LimitDefinition.TryParseShape"/> hands <see cref="TryParse"/> only trimmed text that is neither
/// empty nor <c>off</c>.</para>
/// </remarks>
/// <seealso cref="LimitDefinition"/>
public interface ILimitUnit
{
    /// <summary>A valid value text of this unit, for example <c>256 MiB</c>; shown in hints and refusals.</summary>
    string Example { get; }

    /// <summary>
    /// The smallest minimum a definition of this unit may declare: 0, or 1 where a zero value would refuse everything at once
    /// (a duration: "no wait" is spelled <c>off</c>).
    /// </summary>
    long SmallestMinimum { get; }

    /// <summary>
    /// Endings a key must not have, because the unit belongs to the value and not to the key (case-sensitive, for example
    /// <c>Bytes</c>, <c>_ms</c>): a key that names its unit lies once the unit changes. Empty for none.
    /// </summary>
    IReadOnlyList<string> ForbiddenKeySuffixes { get; }

    /// <summary>Parses the text of a value into the base unit.</summary>
    /// <param name="text">The text: trimmed, not empty, not <c>off</c>.</param>
    /// <param name="maximum">The largest value of the definition in the base unit; a refusal of a number that is too large names it.</param>
    /// <param name="value">The value in the base unit (not negative) when the method returns <see langword="true"/>.</param>
    /// <param name="problem">What is wrong and what to write instead, when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the text is exactly representable; the range of the definition is not checked here.</returns>
    bool TryParse(ReadOnlySpan<char> text, long maximum, out long value, [NotNullWhen(false)] out string? problem);

    /// <summary>Formats a value of the base unit in the largest word that represents it exactly.</summary>
    /// <param name="value">A non-negative value in the base unit.</param>
    /// <returns>For example <c>256 MiB</c>; the text parses back to <paramref name="value"/>.</returns>
    string Format(long value);
}
