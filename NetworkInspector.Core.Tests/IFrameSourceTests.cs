// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Covers default interface members on <see cref="IFrameSource"/>.
/// </summary>
internal sealed class IFrameSourceTests
{
    [Test]
    public async Task EstimatedFrameCount_CanBeNull()
    {
        using ProbeSource source = new();
        IFrameSource iface = source;
        await Assert.That(iface.EstimatedFrameCount).IsNull();
    }

    private sealed class ProbeSource : IFrameSource
    {
        public string UiName => "probe";
        public string? Description => null;
        public int? EstimatedFrameCount => null;
        public bool IsRunning => false;

        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
        {
        }

        public Frame? NextFrame(CancellationToken cancellationToken = default) => null;

        public void Dispose()
        {
        }
    }
}
