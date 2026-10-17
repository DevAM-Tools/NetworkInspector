// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using ByteSlab = NetworkInspector.Core.Fields.SlabAllocator<byte>;

namespace NetworkInspector.Sources;

/// <summary>
/// Hands out frame buffers as slices of fixed-size slabs, so a sequential read allocates one array per slab
/// instead of one array per frame. Built on the Core bump allocator (<see cref="NetworkInspector.Core.Fields.SlabAllocator{T}"/>, link-compiled)
/// with the frame policy on top: automatic rollover to a fresh slab, a private array for big frames, and
/// uninitialised backing memory because every frame byte is written before the frame is published.
/// </summary>
/// <remarks>
/// <para>
/// <b>Retention:</b> a slab stays alive while any frame references it. A consumer that holds one frame keeps at most
/// one slab (<see cref="SlabSize"/> bytes) alive; frames above a quarter of the slab get their own array so a single
/// jumbo frame neither wastes most of a slab nor pins it.
/// </para>
/// <para>
/// <b>Thread-safety:</b> not thread-safe. One instance per sequential reader; it is only used on the thread that
/// calls <c>NextFrame</c>. Random access (<c>FrameById</c>) never uses it.
/// </para>
/// </remarks>
internal sealed class FrameSlabAllocator
{
    #region Constants

    /// <summary>Smallest slab size the options accept (1 KiB).</summary>
    internal const int MinSlabSize = 1024;

    /// <summary>Largest slab size the options accept (16 MiB): bounds what one held frame can keep alive.</summary>
    internal const int MaxSlabSize = 16 * 1024 * 1024;

    /// <summary>Frames longer than <c>SlabSize / BypassDivisor</c> bypass the slab.</summary>
    internal const int BypassDivisor = 4;

    #endregion

    #region Fields

    /// <summary>Current slab; replaced when the rest does not fit. Old slabs live on through their frames.</summary>
    private ByteSlab _Slab;

    #endregion

    #region Constructors

    /// <summary>Creates an allocator with <paramref name="slabSize"/>-byte slabs.</summary>
    /// <param name="slabSize">Slab size in bytes, <see cref="MinSlabSize"/> to <see cref="MaxSlabSize"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slabSize"/> is outside the accepted range.</exception>
    internal FrameSlabAllocator(int slabSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slabSize, MinSlabSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slabSize, MaxSlabSize);
        SlabSize = slabSize;
        _Slab = new(slabSize, uninitialized: true);
    }

    #endregion

    #region Properties

    /// <summary>Slab size in bytes.</summary>
    internal int SlabSize
    {
        get;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Validates a <c>FrameSlabSize</c> option value: 0 (off) or <see cref="MinSlabSize"/> to <see cref="MaxSlabSize"/>.
    /// Called from the option setters.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative, between 1 and 1023, or above 16 MiB.</exception>
    internal static int ValidateOption(int slabSize, [CallerArgumentExpression(nameof(slabSize))] string? paramName = null)
    {
        if (slabSize == 0)
        {
            return 0;
        }

        if (slabSize < MinSlabSize || slabSize > MaxSlabSize)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                slabSize,
                $"FrameSlabSize must be 0 (off) or between {MinSlabSize} and {MaxSlabSize} bytes.");
        }

        return slabSize;
    }

    /// <summary>Creates an allocator for an option value; <see langword="null"/> for 0 (per-frame arrays).</summary>
    internal static FrameSlabAllocator? CreateOrNull(int slabSize) => slabSize == 0
        ? null
        : new FrameSlabAllocator(slabSize);

    /// <summary>
    /// Returns a zero-filled writable region of <paramref name="length"/> bytes: a slab region when
    /// <paramref name="slabs"/> is set, otherwise a new array. For frame builders that do not write every byte
    /// (reserved header bytes, padding), so they behave the same with and without slabs.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    internal static Memory<byte> RentZeroed(FrameSlabAllocator? slabs, int length)
    {
        if (slabs is null)
        {
            return new byte[length];
        }

        Memory<byte> region = slabs.Allocate(length);
        region.Span.Clear();
        return region;
    }

    /// <summary>
    /// Adapter for the per-frame-array convenience overloads of the frame parsers: returns the array behind
    /// <paramref name="frame"/> (built without slabs, so it covers the whole array) when <paramref name="parsed"/> is true.
    /// </summary>
    internal static bool ToArray(bool parsed, ReadOnlyMemory<byte> frame, out byte[] array)
    {
        if (!parsed)
        {
            array = [];
            return false;
        }

        array = MemoryMarshal.TryGetArray(frame, out ArraySegment<byte> segment)
            && segment.Offset == 0
            && segment.Count == segment.Array!.Length
            ? segment.Array
            : frame.ToArray();
        return true;
    }

    /// <summary>
    /// Returns a writable region of exactly <paramref name="length"/> bytes. The bytes are not initialised:
    /// the caller writes every byte before the region is published in a frame.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    internal Memory<byte> Allocate(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length == 0)
        {
            return Memory<byte>.Empty;
        }

        // Big frames get their own array: one jumbo frame must not evict the slab for all its neighbours.
        if (length > SlabSize / BypassDivisor)
        {
            return GC.AllocateUninitializedArray<byte>(length);
        }

        // Bump inside the current slab; on exhaustion start a fresh one. The old slab is not referenced by the
        // allocator any more, so it dies with its last frame. length <= SlabSize / 4 always fits a fresh slab.
        if (!_Slab.TryAllocate(length, out byte[] buffer, out int offset))
        {
            _Slab = new(SlabSize, uninitialized: true);
            _ = _Slab.TryAllocate(length, out buffer, out offset);
        }

        return buffer.AsMemory(offset, length);
    }

    #endregion
}
