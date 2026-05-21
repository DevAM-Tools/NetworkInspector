// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder.Tests;

/// <summary>
/// Boundary and validation tests for <see cref="DhcpV4Layer"/> and
/// <see cref="DhcpV6Layer"/> constructors.
/// </summary>
internal sealed class DhcpLayerValidationTests
{
    #region DhcpV4Layer — null options

    [Test]
    public async Task DhcpV4Layer_NullOptions_Throws()
    {
        bool threw = false;
        try
        {
            _ = new DhcpV4Layer(1, 0x12345678, null!);
        }
        catch (ArgumentNullException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    #endregion

    #region DhcpV4Layer — option data length

    [Test]
    public async Task DhcpV4Layer_OptionDataExceeds255_Throws()
    {
        DhcpV4Option oversized = new(53, new byte[256]);
        bool threw = false;
        try
        {
            _ = new DhcpV4Layer(1, 0, [oversized]);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task DhcpV4Layer_OptionDataMax255_Succeeds()
    {
        DhcpV4Option opt = new(43, new byte[255]);
        DhcpV4Layer layer = new(1, 0, [opt]);
        await Assert.That(layer.HeaderSize).IsGreaterThan(0);
    }

    #endregion

    #region DhcpV6Layer — null options

    [Test]
    public async Task DhcpV6Layer_NullOptions_Throws()
    {
        bool threw = false;
        try
        {
            _ = new DhcpV6Layer(1, 0, null!);
        }
        catch (ArgumentNullException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    #endregion

    #region DhcpV6Layer — option data length

    [Test]
    public async Task DhcpV6Layer_OptionDataExceeds65535_Throws()
    {
        DhcpV6Option oversized = new(1, new byte[65536]);
        bool threw = false;
        try
        {
            _ = new DhcpV6Layer(1, 0, [oversized]);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task DhcpV6Layer_OptionDataMax65535_Succeeds()
    {
        DhcpV6Option opt = new(1, new byte[65535]);
        DhcpV6Layer layer = new(1, 0, [opt]);
        await Assert.That(layer.HeaderSize).IsGreaterThan(0);
    }

    #endregion
}
