// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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
