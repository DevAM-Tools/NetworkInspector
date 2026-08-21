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
internal readonly record struct LocationInfo(string FilePath, TextSpan TextSpan, LinePositionSpan LineSpan)
{
    #region Factories

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

    #endregion

    #region Methods

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

    #endregion
}
