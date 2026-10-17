// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using TcpConnectionKey = NetworkInspector.Protocols.Tcp.TcpConnectionKey;
using UdpConnectionKey = NetworkInspector.Protocols.Udp.UdpConnectionKey;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Hashing of the UDP and TCP connection keys: both directions hash alike, and keys built to collide under the former
/// unkeyed XOR-rotate mix land in different buckets (the hash is seeded per process).
/// </summary>
internal sealed class ConnectionKeyHashTests
{
    #region Helpers

    /// <summary>
    /// Two key address pairs that had equal XOR-rotate hashes: flipping bit 0 of the second address's low half was
    /// undone by flipping bit 7 of the first address's high half.
    /// </summary>
    private static (UInt128 First1, UInt128 First2, UInt128 Second1, UInt128 Second2) _CraftedCollision() =>
        (new UInt128(1, 0), new UInt128(1000, 0), new UInt128(1 ^ (1UL << 7), 0), new UInt128(1000, 1));

    #endregion

    #region Tests

    [Test]
    public async Task UdpKeyBothDirectionsHashAlike()
    {
        UdpConnectionKey forward = UdpConnectionKey.FromIPv4(0xAC100164, 0xAC100101, 12345, 53);
        UdpConnectionKey reverse = UdpConnectionKey.FromIPv4(0xAC100101, 0xAC100164, 53, 12345);

        await Assert.That(forward).IsEqualTo(reverse);
        await Assert.That(forward.GetHashCode()).IsEqualTo(reverse.GetHashCode());
    }

    [Test]
    public async Task TcpKeyBothDirectionsHashAlike()
    {
        TcpConnectionKey forward = TcpConnectionKey.FromIPv4(0x0A000001, 0x0A000002, 49152, 80, lastVlanId: 7, frameIdentity: 3);
        TcpConnectionKey reverse = TcpConnectionKey.FromIPv4(0x0A000002, 0x0A000001, 80, 49152, lastVlanId: 7, frameIdentity: 3);

        await Assert.That(forward).IsEqualTo(reverse);
        await Assert.That(forward.GetHashCode()).IsEqualTo(reverse.GetHashCode());
    }

    [Test]
    public async Task UdpKeysCraftedToCollideUnderTheUnkeyedMixHashApart()
    {
        (UInt128 first1, UInt128 first2, UInt128 second1, UInt128 second2) = _CraftedCollision();
        UdpConnectionKey first = new(first1, first2, 10, 20);
        UdpConnectionKey second = new(second1, second2, 10, 20);

        await Assert.That(first).IsNotEqualTo(second);
        await Assert.That(first.GetHashCode()).IsNotEqualTo(second.GetHashCode());
    }

    [Test]
    public async Task TcpKeysCraftedToCollideUnderTheUnkeyedMixHashApart()
    {
        (UInt128 first1, UInt128 first2, UInt128 second1, UInt128 second2) = _CraftedCollision();
        TcpConnectionKey first = new(first1, first2, 10, 20);
        TcpConnectionKey second = new(second1, second2, 10, 20);

        await Assert.That(first).IsNotEqualTo(second);
        await Assert.That(first.GetHashCode()).IsNotEqualTo(second.GetHashCode());
    }

    [Test]
    public async Task TcpKeyVlanAndFrameIdentityChangeTheHash()
    {
        TcpConnectionKey plain = TcpConnectionKey.FromIPv4(1, 2, 10, 20);
        TcpConnectionKey vlan = TcpConnectionKey.FromIPv4(1, 2, 10, 20, lastVlanId: 7);
        TcpConnectionKey frame = TcpConnectionKey.FromIPv4(1, 2, 10, 20, frameIdentity: 3);

        await Assert.That(plain.GetHashCode()).IsNotEqualTo(vlan.GetHashCode());
        await Assert.That(plain.GetHashCode()).IsNotEqualTo(frame.GetHashCode());
    }

    #endregion
}
