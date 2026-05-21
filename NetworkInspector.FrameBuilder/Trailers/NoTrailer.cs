// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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
