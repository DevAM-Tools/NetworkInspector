// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for the per-protocol thread-local address caches embedded directly in
/// <see cref="IPv4Protocol"/>, <see cref="IPv6Protocol"/>, and
/// <see cref="EthernetProtocol"/>.
/// <para>
/// Verifies write/read round-trips, PacketId validation (stale entry rejection),
/// tunnel overwrite behaviour (innermost layer wins), and independence of the
/// IPv4 and IPv6 caches for the same packet.
/// </para>
/// <para>
/// <b>Thread-safety note:</b> Each cache uses [ThreadStatic]. Tests run on a
/// single thread, so each test sees isolated cache state within the test run.
/// </para>
/// </summary>
internal sealed class ProtocolCacheTests
{
    #region IPv4Protocol cache

    [Test]
    public async Task IPv4Cache_Set_ThenGet_SamePacket_ReturnsAddresses()
    {
        PacketId id = new(42);
        IPv4Address src = new(0x0A000001);
        IPv4Address dst = new(0x0A000002);
        IPv4Protocol.SetCachedAddresses(id, src, dst);

        bool found = IPv4Protocol.TryGetCachedAddresses(id, out IPv4Address outSrc, out IPv4Address outDst);
        await Assert.That(found).IsTrue();
        await Assert.That(outSrc).IsEqualTo(src);
        await Assert.That(outDst).IsEqualTo(dst);
    }

    [Test]
    public async Task IPv4Cache_Get_DifferentPacketId_ReturnsFalse()
    {
        IPv4Protocol.SetCachedAddresses(new PacketId(10), new IPv4Address(1), new IPv4Address(2));

        bool found = IPv4Protocol.TryGetCachedAddresses(new PacketId(11), out _, out _);
        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task IPv4Cache_OverwrittenByNewerPacket_ReturnsNewData()
    {
        IPv4Protocol.SetCachedAddresses(new PacketId(1), new IPv4Address(0x01), new IPv4Address(0x02));
        IPv4Protocol.SetCachedAddresses(new PacketId(2), new IPv4Address(0xAA), new IPv4Address(0xBB));

        bool oldFound = IPv4Protocol.TryGetCachedAddresses(new PacketId(1), out _, out _);
        await Assert.That(oldFound).IsFalse();

        bool newFound = IPv4Protocol.TryGetCachedAddresses(new PacketId(2), out IPv4Address outSrc, out IPv4Address outDst);
        await Assert.That(newFound).IsTrue();
        await Assert.That(outSrc.RawValue).IsEqualTo(0xAAu);
        await Assert.That(outDst.RawValue).IsEqualTo(0xBBu);
    }

    [Test]
    public async Task IPv4Cache_TunnelOverwrite_InnermostWins()
    {
        // Simulates Ethernet → outer IPv4 → inner IPv4 (IP-in-IP tunnel).
        // The inner IPv4 layer calls Set last; UDP reads the inner (correct) addresses.
        PacketId id = new(99);
        IPv4Protocol.SetCachedAddresses(id, new IPv4Address(0xC0A80001), new IPv4Address(0xC0A80002)); // outer
        IPv4Protocol.SetCachedAddresses(id, new IPv4Address(0x0A000001), new IPv4Address(0x0A000002)); // inner

        bool found = IPv4Protocol.TryGetCachedAddresses(id, out IPv4Address outSrc, out IPv4Address outDst);
        await Assert.That(found).IsTrue();
        await Assert.That(outSrc.RawValue).IsEqualTo(0x0A000001u); // inner
        await Assert.That(outDst.RawValue).IsEqualTo(0x0A000002u); // inner
    }

    #endregion

    #region IPv6Protocol cache

    [Test]
    public async Task IPv6Cache_Set_ThenGet_SamePacket_ReturnsAddresses()
    {
        PacketId id = new(50);
        IPv6Address src = new(0x2001_0DB8_0000_0001UL, 0x0000_0000_0000_0001UL);
        IPv6Address dst = new(0x2001_0DB8_0000_0002UL, 0x0000_0000_0000_0002UL);
        IPv6Protocol.SetCachedAddresses(id, src, dst);

        bool found = IPv6Protocol.TryGetCachedAddresses(id, out IPv6Address outSrc, out IPv6Address outDst);
        await Assert.That(found).IsTrue();
        await Assert.That(outSrc).IsEqualTo(src);
        await Assert.That(outDst).IsEqualTo(dst);
    }

    [Test]
    public async Task IPv6Cache_Get_DifferentPacketId_ReturnsFalse()
    {
        IPv6Protocol.SetCachedAddresses(new PacketId(60), new IPv6Address(1UL, 2UL), new IPv6Address(3UL, 4UL));

        bool found = IPv6Protocol.TryGetCachedAddresses(new PacketId(61), out _, out _);
        await Assert.That(found).IsFalse();
    }

    #endregion

    #region EthernetProtocol cache

    [Test]
    public async Task EthernetCache_Set_ThenGet_SamePacket_ReturnsAddresses()
    {
        PacketId id = new(70);
        MacAddress src = new(0x0011_2233_4455UL);
        MacAddress dst = new(0xAABB_CCDD_EEFFUL);
        EthernetProtocol.SetCachedAddresses(id, src, dst);

        bool found = EthernetProtocol.TryGetCachedAddresses(id, out MacAddress outSrc, out MacAddress outDst);
        await Assert.That(found).IsTrue();
        await Assert.That(outSrc).IsEqualTo(src);
        await Assert.That(outDst).IsEqualTo(dst);
    }

    [Test]
    public async Task EthernetCache_Get_DifferentPacketId_ReturnsFalse()
    {
        EthernetProtocol.SetCachedAddresses(new PacketId(80), new MacAddress(1UL), new MacAddress(2UL));

        bool found = EthernetProtocol.TryGetCachedAddresses(new PacketId(81), out _, out _);
        await Assert.That(found).IsFalse();
    }

    #endregion

    #region IPv4 and IPv6 caches are independent

    [Test]
    public async Task IPv4Cache_AndIPv6Cache_BothValid_SamePacket()
    {
        // Both caches are independent; setting one does not invalidate the other.
        PacketId id = new(200);
        IPv4Protocol.SetCachedAddresses(id, new IPv4Address(0x01020304), new IPv4Address(0x05060708));
        IPv6Protocol.SetCachedAddresses(id, new IPv6Address(1UL, 2UL), new IPv6Address(3UL, 4UL));

        bool ipv4Found = IPv4Protocol.TryGetCachedAddresses(id, out _, out _);
        bool ipv6Found = IPv6Protocol.TryGetCachedAddresses(id, out _, out _);
        await Assert.That(ipv4Found).IsTrue();
        await Assert.That(ipv6Found).IsTrue();
    }

    #endregion
}
