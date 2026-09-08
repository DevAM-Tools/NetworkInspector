// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// Construction options for <see cref="ValueCache"/>.
/// <see cref="RecordAllFields"/> records a payload series for every field that actually appears
/// (including <see cref="FieldType.None"/> containers when
/// <see cref="RecordContainerPresence"/> is set). It does not pre-create empty
/// series for unused stack fields, and it does not auto-create custom-text or custom-representation
/// series. Explicit field configs add those series at construction and override capture mode.
/// Pass <see langword="null"/> or omit the argument on <see cref="ValueCache"/> to get these defaults.
/// Do not pass <c>default(ValueCacheBuildOptions)</c>: that zeroes <see cref="ChunkShift"/> and is rejected.
/// </summary>
public readonly struct ValueCacheBuildOptions
{
    #region Constants

    /// <summary>Inclusive minimum <see cref="ChunkShift"/>. Same bound as <see cref="ChunkedOuterArray{TChunk}"/>.</summary>
    public const int MinChunkShift = 4;

    /// <summary>Inclusive maximum <see cref="ChunkShift"/>. Same bound as <see cref="ChunkedOuterArray{TChunk}"/>.</summary>
    public const int MaxChunkShift = 20;

    #endregion

    #region Fields

    /// <summary>Backing store for <see cref="ChunkShift"/>. Default 12 is set in the parameterless constructor.</summary>
    private readonly int _ChunkShift;

    #endregion

    #region Lifecycle

    /// <summary>Defaults: <see cref="ChunkShift"/> 12, <see cref="DefaultCaptureMode"/> first occurrence, record flags false.</summary>
    public ValueCacheBuildOptions()
    {
        DefaultCaptureMode = ValueCaptureMode.FirstOccurrence;
        _ChunkShift = 12;
    }

    #endregion

    #region Properties

    /// <summary>
    /// When true, every field that is recorded into this cache gets a payload series.
    /// Unused <see cref="Stack.Fields"/> entries stay absent from <see cref="ValueCache.Series"/>.
    /// </summary>
    public bool RecordAllFields { get; init; }

    /// <summary>Capture mode used for <see cref="RecordAllFields"/> payload series and for group expansion defaults.</summary>
    public ValueCaptureMode DefaultCaptureMode { get; init; }

    /// <summary>
    /// When true, <see cref="RecordAllFields"/> also creates a presence series for
    /// <see cref="FieldType.None"/> containers. Default false: container presence is a packet-index
    /// concern. Explicit field configs still record those fields.
    /// </summary>
    public bool RecordContainerPresence { get; init; }

    /// <summary>
    /// Log₂ of rows per inner column chunk. Default 12 (4096). Allowed range
    /// <see cref="MinChunkShift"/>…<see cref="MaxChunkShift"/>. Choose a smaller
    /// shift for short captures so the first chunk is not 4096 slots. Passed into every series store.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is outside <see cref="MinChunkShift"/>…<see cref="MaxChunkShift"/>.</exception>
    public int ChunkShift
    {
        get => _ChunkShift;
        init
        {
            ThrowIfChunkShiftOutOfRange(value, nameof(ChunkShift));
            _ChunkShift = value;
        }
    }

    #endregion

    #region Helpers

    /// <summary>Throws when <paramref name="chunkShift"/> is outside <see cref="MinChunkShift"/>…<see cref="MaxChunkShift"/>.</summary>
    /// <param name="chunkShift">Candidate log₂ chunk size.</param>
    /// <param name="paramName">Argument name for the exception.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkShift"/> is out of range.</exception>
    internal static void ThrowIfChunkShiftOutOfRange(int chunkShift, string paramName)
    {
        if ((uint)(chunkShift - MinChunkShift) > (uint)(MaxChunkShift - MinChunkShift))
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                chunkShift,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"ChunkShift must be between {MinChunkShift} and {MaxChunkShift}."));
        }
    }

    #endregion
}
