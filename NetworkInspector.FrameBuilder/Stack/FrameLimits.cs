// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Hard limits shared by <see cref="FrameSequence{TStack,TTrailer,TInterceptor}"/>
/// and <see cref="StatefulFrameSequence{TStack,TTrailer,TInterceptor}"/>.
/// </summary>
internal static class FrameLimits
{
    /// <summary>
    /// Maximum supported cons-list depth.  Stacks deeper than this surface
    /// <see cref="BuildStatus.StackTooDeep"/> instead of allocating.
    /// </summary>
    internal const int MaxSupportedDepth = 32;
}
