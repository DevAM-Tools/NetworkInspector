// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Exit-point coverage for <see cref="FrameSourceInfo"/>.
/// </summary>
internal sealed class FrameSourceInfoTests
{
    [Test]
    public async Task UiName_WithoutSource_FallsBackToIdString()
    {
        FrameSourceInfo info = new(new FrameSourceId(7), source: null);
        await Assert.That(info.UiName).IsEqualTo("7");
    }

    [Test]
    public async Task UiName_WithSource_DelegatesToSource()
    {
        using DescribedSource source = new("My Capture", "details");
        FrameSourceInfo info = new(new FrameSourceId(1), source);
        await Assert.That(info.UiName).IsEqualTo("My Capture");
        await Assert.That(info.Description).IsEqualTo("details");
    }

    [Test]
    public async Task Stop_InvokesRegisteredCallback()
    {
        using DescribedSource source = new("src", null);
        FrameSourceInfo info = new(new FrameSourceId(0), source);
        int calls = 0;
        info.RegisterStopCallback(() => calls++);

        await Assert.That(info.IsStoppable).IsTrue();
        info.Stop();
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(info.IsStoppable).IsFalse();
    }

    [Test]
    public async Task Stop_SecondCall_DoesNotReinvokeCallback()
    {
        using DescribedSource source = new("src", null);
        FrameSourceInfo info = new(new FrameSourceId(0), source);
        int calls = 0;
        info.RegisterStopCallback(() => calls++);

        info.Stop();
        info.Stop();
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(info.IsStoppable).IsFalse();
    }

    [Test]
    public async Task RegisterStopCallback_SecondRegistration_Throws()
    {
        using DescribedSource source = new("src", null);
        FrameSourceInfo info = new(new FrameSourceId(0), source);
        info.RegisterStopCallback(() => { });

        await Assert.That(() => info.RegisterStopCallback(() => { }))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Stop_WithoutCallback_IsNoOp()
    {
        FrameSourceInfo info = new(new FrameSourceId(0), null);
        await Assert.That(info.IsStoppable).IsFalse();
        info.Stop();
        await Assert.That(info.Description).IsNull();
    }

    private sealed class DescribedSource(string uiName, string? description) : IFrameSource
    {
        public string UiName => uiName;
        public string? Description => description;
        public int? EstimatedFrameCount => null;
        public bool IsRunning => false;

        public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
        {
        }

        public Frame? NextFrame(CancellationToken cancellationToken = default) => null;

        public void Stop()
        {
            _ = UiName;
        }

        public void Dispose()
        {
        }
    }
}
