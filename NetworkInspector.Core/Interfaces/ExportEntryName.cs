// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Interfaces;

/// <summary>
/// The name of one entry of an export: <see cref="Main"/> for the single output of a single-file format, or a relative name such
/// as <c>packets.parquet</c> or <c>fields/field_12.parquet</c>. Immutable.
/// <para>
/// Grammar of a named entry: segments joined by <c>/</c>; a segment is 1 to <see cref="MaxSegmentLength"/> characters of
/// <c>a</c>–<c>z</c>, <c>0</c>–<c>9</c>, <c>_</c>, <c>-</c>, and <c>.</c>, does not start or end with <c>.</c>, and its part before
/// the first <c>.</c> is none of the device names <c>con</c>, <c>prn</c>, <c>aux</c>, <c>nul</c>, <c>com0</c>–<c>com9</c>,
/// <c>lpt0</c>–<c>lpt9</c>.
/// </para>
/// </summary>
/// <remarks>
/// Security: the name reaches the host's file system. Threat: an entry that escapes the target root (<c>..</c>, a root, a
/// backslash, a drive or stream colon) or opens a device (<c>nul</c> on Windows). Mitigation: the closed grammar above, checked
/// once in <see cref="TryCreate"/>; a directory target also resolves the path and confirms it stays under its root. Lower case
/// only, so two names never meet as one file on a case-insensitive file system. Rejected: accepting any text and leaving the check
/// to each target, which a third-party target could forget. Residual risk: none known for the listed file systems.
/// </remarks>
/// <seealso cref="IExportTarget"/>
public readonly struct ExportEntryName : IEquatable<ExportEntryName>
{
    /// <summary>
    /// The longest segment: 255 characters, the component length limit of NTFS, ext4, and APFS. A fact of the target file
    /// systems, not a limit: a longer segment cannot be stored by any of them, so it is a constant and no limit definition.
    /// </summary>
    public const int MaxSegmentLength = 255;

    #region Value

    /// <summary>The text; <see langword="null"/> for <see cref="Main"/>, which is also <c>default</c>.</summary>
    private readonly string? _Text;

    /// <summary>Creates a named entry from a text that <see cref="TryCreate"/> already validated.</summary>
    /// <param name="text">A text that matches the grammar of the type.</param>
    private ExportEntryName(string text) => _Text = text;

    /// <summary>The single output of a single-file format; equals <c>default(ExportEntryName)</c>.</summary>
    public static ExportEntryName Main => default;

    /// <summary>Whether this is <see cref="Main"/>.</summary>
    public bool IsMain => _Text is null;

    /// <summary>The relative name with <c>/</c> as separator, for example <c>fields/field_12.parquet</c>; empty for <see cref="Main"/>.</summary>
    public string Text => _Text
        ?? string.Empty;

    #endregion

    #region Name grammar

    /// <summary>Creates a named entry from <paramref name="text"/> when it matches the grammar of the type.</summary>
    /// <param name="text">The relative name, segments joined by <c>/</c>. Never <see cref="Main"/>: the empty text is refused.</param>
    /// <param name="name">The entry when the method returns <see langword="true"/>; <see cref="Main"/> otherwise.</param>
    /// <param name="problem">The broken rule in one sentence that names the text, <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when the text is a valid entry name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>: a bug of the caller.</exception>
    public static bool TryCreate(string text, out ExportEntryName name, [NotNullWhen(false)] out string? problem)
    {
        // A null text is a bug of the caller; an empty text is a refused name like any other.
        ArgumentNullException.ThrowIfNull(text);
        name = default;
        if (text.Length == 0)
        {
            problem = "An entry name must not be empty.";
            return false;
        }

        // Walk the segments between the slashes; a leading, trailing, or double slash yields an empty segment.
        int start = 0;
        while (true)
        {
            int slash = text.IndexOf('/', start);
            int end = slash < 0
                ? text.Length
                : slash;
            if (!_TryCheckSegment(text.AsSpan(start, end - start), out string? segmentProblem))
            {
                problem = Invariant($"Entry name '{text}' {segmentProblem}");
                return false;
            }

            if (slash < 0)
            {
                break;
            }

            start = slash + 1;
        }

        name = new ExportEntryName(text);
        problem = null;
        return true;
    }

    /// <summary>Checks one segment against the grammar of the type.</summary>
    /// <param name="segment">The characters between two slashes (or the ends).</param>
    /// <param name="problem">The broken rule as the end of a sentence ("has an empty segment."); <see langword="null"/> when valid.</param>
    /// <returns><see langword="true"/> when the segment is valid.</returns>
    private static bool _TryCheckSegment(ReadOnlySpan<char> segment, [NotNullWhen(false)] out string? problem)
    {
        if (segment.IsEmpty)
        {
            problem = "has an empty segment.";
            return false;
        }

        if (segment.Length > MaxSegmentLength)
        {
            problem = Invariant($"has a segment of {segment.Length} characters; at most {MaxSegmentLength} are allowed.");
            return false;
        }

        foreach (char character in segment)
        {
            bool allowed = character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' or '.';
            if (!allowed)
            {
                problem = Invariant($"contains '{character}'; only a-z, 0-9, '_', '-', and '.' are allowed.");
                return false;
            }
        }

        // "." and ".." are covered here; Windows also drops a trailing dot, so "a." and "a" would meet as one file.
        if (segment[0] == '.' || segment[^1] == '.')
        {
            problem = "has a segment that starts or ends with '.'.";
            return false;
        }

        // Windows opens a device for these stems with any extension ("nul.parquet"); refused on every platform, so an
        // export that works on Linux also works on Windows.
        int dot = segment.IndexOf('.');
        ReadOnlySpan<char> stem = dot < 0
            ? segment
            : segment[..dot];
        if (_IsDeviceName(stem))
        {
            problem = Invariant($"names the device '{stem.ToString()}'.");
            return false;
        }

        problem = null;
        return true;
    }

    /// <summary>Whether <paramref name="stem"/> (lower case by the grammar) is a Windows device name.</summary>
    /// <param name="stem">The part of a segment before its first dot.</param>
    /// <returns><see langword="true"/> for <c>con</c>, <c>prn</c>, <c>aux</c>, <c>nul</c>, <c>com0</c>–<c>com9</c>, <c>lpt0</c>–<c>lpt9</c>.</returns>
    private static bool _IsDeviceName(ReadOnlySpan<char> stem) =>
        stem is "con" or "prn" or "aux" or "nul"
        || (stem.Length == 4 && (stem.StartsWith("com", StringComparison.Ordinal) || stem.StartsWith("lpt", StringComparison.Ordinal))
            && char.IsAsciiDigit(stem[3]));

    #endregion

    #region Equality and text

    /// <summary>Whether both names have the same text (ordinal); <see cref="Main"/> equals only <see cref="Main"/>.</summary>
    /// <param name="other">The name to compare with.</param>
    /// <returns><see langword="true"/> when the names are equal.</returns>
    public bool Equals(ExportEntryName other) => string.Equals(_Text, other._Text, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ExportEntryName other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return _Text is null
            ? 0
            : string.GetHashCode(_Text, StringComparison.Ordinal);
    }

    /// <summary>The text of the entry for messages.</summary>
    /// <returns><see cref="Text"/>, or <c>(main)</c> for <see cref="Main"/>.</returns>
    public override string ToString()
    {
        return _Text
            ?? "(main)";
    }

    /// <summary>Whether two names are equal; see <see cref="Equals(ExportEntryName)"/>.</summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    /// <returns><see langword="true"/> when the names are equal.</returns>
    public static bool operator ==(ExportEntryName left, ExportEntryName right) => left.Equals(right);

    /// <summary>Whether two names differ; see <see cref="Equals(ExportEntryName)"/>.</summary>
    /// <param name="left">The first name.</param>
    /// <param name="right">The second name.</param>
    /// <returns><see langword="true"/> when the names differ.</returns>
    public static bool operator !=(ExportEntryName left, ExportEntryName right) => !left.Equals(right);

    #endregion
}
