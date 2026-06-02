// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Shared geometry helpers for <see cref="FrameSequence{TValues,TTrailer,TInterceptor}"/>
/// and <see cref="StatefulFrameSequence{TValues,TTrailer,TInterceptor}"/>.
/// </summary>
/// <remarks>
/// Both frame-sequence types share identical fragmentation geometry validation and
/// per-fragment slice setup.  This class centralises those calculations to keep the
/// two types in sync.
/// </remarks>
internal static class FragmentGeometryHelper
{
    /// <summary>
    /// Validates fragmentation geometry, computes the maximum inner-fragment payload
    /// length, and fills <paramref name="maxFragInner"/> and <paramref name="innerLen"/>.
    /// </summary>
    /// <param name="canFragment">Whether the innermost fragmentable layer permits fragmentation.</param>
    /// <param name="alignment">Required fragment alignment reported by the layer.</param>
    /// <param name="headerEndOffset">Byte offset past the last header byte of the fragmentable layer.</param>
    /// <param name="dataLength">Total byte length of headers plus payload.</param>
    /// <param name="maxFrameLen">Maximum frame length (MTU) including trailer.</param>
    /// <param name="trailerSize">Trailer size in bytes.</param>
    /// <param name="innerLen">Receives the total inner-payload length.</param>
    /// <param name="maxFragInner">Receives the per-fragment inner-payload length (aligned down).</param>
    /// <returns>A <see cref="BuildStatus"/> of <see cref="BuildStatus.Success"/> on success,
    /// or the appropriate failure code.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static BuildStatus TryComputeFragmentGeometry(
        bool canFragment,
        int alignment,
        int headerEndOffset,
        int dataLength,
        int maxFrameLen,
        int trailerSize,
        out int innerLen,
        out int maxFragInner)
    {
        innerLen = 0;
        maxFragInner = 0;

        if (!canFragment)
        {
            return BuildStatus.FragmentationRequired;
        }

        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
        {
            return BuildStatus.InvalidLayerState;
        }

        int maxFragInnerRaw = maxFrameLen - headerEndOffset - trailerSize;
        if (maxFragInnerRaw < alignment)
        {
            return BuildStatus.FragmentationRequired;
        }

        innerLen = dataLength - headerEndOffset;
        // The guard above guarantees maxFragInnerRaw >= alignment, so rounding
        // down to the nearest alignment multiple always yields at least one full
        // alignment unit (maxFragInner >= alignment > 0). A zero per-fragment
        // slice is therefore unreachable here and downstream loops always advance.
        maxFragInner = maxFragInnerRaw & ~(alignment - 1);
        return BuildStatus.Success;
    }

    /// <summary>
    /// Initialises a <see cref="PostFixContext"/> with the supplied layer geometry.
    /// </summary>
    /// <remarks>
    /// Callers must ensure <paramref name="psSrc"/> and <paramref name="psDst"/> are
    /// stack-allocated spans with a lifetime that outlasts the use of <paramref name="ctx"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void InitContext(
        ref PostFixContext ctx,
        Span<byte> psSrc,
        Span<byte> psDst,
        Span<int> offsets,
        int depth,
        int dataLength)
    {
        ctx.PseudoSrcIp = psSrc;
        ctx.PseudoDstIp = psDst;
        ctx.LayerOffsets = offsets;
        ctx.LayerCount = depth;
        ctx.TotalLength = dataLength;
    }
}
