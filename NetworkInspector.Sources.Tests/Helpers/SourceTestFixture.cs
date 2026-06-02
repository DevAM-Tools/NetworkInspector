// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Helpers;

/// <summary>
/// Shared lifecycle helpers used across all source test classes.
/// </summary>
internal static class SourceTestFixture
{
    /// <summary>
    /// Creates a fresh <see cref="FrameInterfaceRegistry"/>, registers <paramref name="source"/>
    /// with it, and calls <see cref="IFrameSource.Start"/>.
    /// Returns the registry so callers that need interface access can use it directly;
    /// callers that do not need it may discard the return value.
    /// </summary>
    /// <param name="source">The frame source to initialize and start.</param>
    /// <returns>The <see cref="FrameInterfaceRegistry"/> the source was registered with.</returns>
    internal static FrameInterfaceRegistry InitializeAndStartSource(IFrameSource source)
    {
        FrameInterfaceRegistry registry = new();
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);
        return registry;
    }
}
