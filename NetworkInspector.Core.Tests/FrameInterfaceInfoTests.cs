// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Exit-point coverage for <see cref="FrameInterfaceInfo"/>.
/// </summary>
internal sealed class FrameInterfaceInfoTests
{
    [Test]
    public async Task Constructor_InvalidUiName_Throws()
    {
        await Assert.That(() => new FrameInterfaceInfo(
                new FrameInterfaceId(0),
                new FrameSourceId(0),
                uiName: "",
                description: null,
                linkType: null))
            .Throws<InvalidUiNameRegistrationException>();
    }

    [Test]
    public async Task Constructor_ValidUiName_Succeeds()
    {
        FrameInterfaceInfo info = new(
            new FrameInterfaceId(0),
            new FrameSourceId(0),
            uiName: "eth0",
            description: null,
            linkType: null);

        await Assert.That(info.UiName).IsEqualTo("eth0");
    }
}
