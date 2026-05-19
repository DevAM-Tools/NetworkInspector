// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Default no-op trailer.  Empty struct: <see cref="TrailerSize"/> is 0 and
/// <see cref="WriteTrailer"/> is empty so the JIT eliminates every call when
/// this type is used as the trailer parameter.
/// </summary>
public readonly struct NoTrailer : ITrailerLayer
{
    /// <inheritdoc />
    public int TrailerSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => 0;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteTrailer(Span<byte> frame, int payloadEnd)
    {
    }
}
