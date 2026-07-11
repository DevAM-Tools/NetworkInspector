// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Generators.Models;

/// <summary>
/// Value-type stand-in for <see cref="Location"/> safe to store inside incremental-pipeline DTOs.
/// <para>
/// Holding a raw <see cref="Location"/> in a cached pipeline value roots the originating
/// <see cref="SyntaxTree"/> (and therefore the entire <see cref="Compilation"/>) for the lifetime
/// of the cache, which leaks memory across edits in long-running IDE sessions. Capturing the
/// minimal information required to reconstruct a <see cref="Location"/> as a flat record sidesteps
/// the issue while still allowing accurate diagnostic placement when the value is finally
/// reported.
/// </para>
/// <para>
/// Equality is structural: two <see cref="LocationInfo"/> instances are equal iff their file path
/// and source span match, so they participate correctly in <c>SequenceEqual</c> on the parent DTO.
/// </para>
/// <para>Thread safety: immutable; safe to share across threads.</para>
/// </summary>
internal readonly struct LocationInfo : IEquatable<LocationInfo>
{
    /// <summary>Absolute path of the source file the location refers to (empty for <see cref="Location.None"/>).</summary>
    public string FilePath
    {
        get;
    }

    /// <summary>Source span (character offsets) within the source file.</summary>
    public TextSpan TextSpan
    {
        get;
    }

    /// <summary>Line/column span derived from <see cref="TextSpan"/>; preserved so that
    /// reconstruction does not require re-parsing the syntax tree.</summary>
    public LinePositionSpan LineSpan
    {
        get;
    }

    /// <summary>Initializes a new <see cref="LocationInfo"/>.</summary>
    public LocationInfo(string filePath, TextSpan textSpan, LinePositionSpan lineSpan)
    {
        FilePath = filePath;
        TextSpan = textSpan;
        LineSpan = lineSpan;
    }

    /// <summary>Creates a <see cref="LocationInfo"/> from a Roslyn <see cref="Location"/>.</summary>
    /// <param name="location">The source location; if <see langword="null"/>, returns a sentinel
    /// pointing at no file.</param>
    public static LocationInfo From(Location? location)
    {
        if (location is null)
        {
            return new LocationInfo(string.Empty, default, default);
        }
        return new LocationInfo(
            location.SourceTree?.FilePath ?? string.Empty,
            location.SourceSpan,
            location.GetLineSpan().Span);
    }

    /// <summary>
    /// Reconstructs a <see cref="Location"/> suitable for diagnostic reporting. Returns
    /// <see cref="Location.None"/> when no file path was captured.
    /// </summary>
    public Location ToLocation()
    {
        if (string.IsNullOrEmpty(FilePath))
        {
            return Location.None;
        }
        return Location.Create(FilePath, TextSpan, LineSpan);
    }

    /// <inheritdoc />
    public bool Equals(LocationInfo other)
        => FilePath == other.FilePath && TextSpan.Equals(other.TextSpan) && LineSpan.Equals(other.LineSpan);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LocationInfo other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = FilePath?.GetHashCode() ?? 0;
            hash = (hash * 397) ^ TextSpan.GetHashCode();
            hash = (hash * 397) ^ LineSpan.GetHashCode();
            return hash;
        }
    }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(LocationInfo left, LocationInfo right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(LocationInfo left, LocationInfo right) => !left.Equals(right);
}
