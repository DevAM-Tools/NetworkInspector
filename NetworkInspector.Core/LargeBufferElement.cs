// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core;

/// <summary>
/// A 16-byte unmanaged building block for <see cref="LargeBuffer"/>.
/// Each element holds exactly two <c>ulong</c> values (<see cref="Low"/> and <see cref="High"/>),
/// totalling 16 bytes. Using this element type instead of a plain <c>ulong</c> doubles the
/// addressable range of <see cref="LargeBuffer"/> from ~16 GB to ~32 GB.
/// </summary>
/// <remarks>
/// <para>
/// The struct is kept deliberately minimal — two primitive fields with a sequential layout —
/// so that it satisfies the C# <c>unmanaged</c> constraint. This allows
/// <see cref="MemoryMarshal.Cast{TFrom,TTo}(Span{TFrom})"/> to reinterpret an array of
/// <see cref="LargeBufferElement"/> as a <c>Span&lt;byte&gt;</c> window without any
/// managed-pointer restrictions.
/// </para>
/// <para>
/// If a larger element size is required in the future (e.g., to extend the capacity beyond
/// 32 GB), add further <c>ulong</c> fields here and update the <see cref="LargeBuffer"/>
/// constants (<c>BytesPerElement</c>, <c>BytesPerElementShift</c>,
/// <c>BytesPerElementMask</c>) accordingly.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct LargeBufferElement
{
    #region Fields

    /// <summary>The low 8 bytes of the element (logical byte offsets 0–7 within the element).</summary>
    internal ulong Low;

    /// <summary>The high 8 bytes of the element (logical byte offsets 8–15 within the element).</summary>
    internal ulong High;

    #endregion
}