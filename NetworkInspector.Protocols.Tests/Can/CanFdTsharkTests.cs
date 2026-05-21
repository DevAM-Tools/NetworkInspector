// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Symmetric tshark cross-validation for the SocketCAN-FD dissector
/// (Plan §3.1.6). Frames are emitted via <see cref="SocketCanFdLayer"/>.
/// Pcap link type 227 (LINKTYPE_CAN_SOCKETCAN); the dissector switches to
/// the canfd protocol when the FDF bit in the flags byte is set.
/// </summary>
/// <remarks>
/// <para>
/// Coverage: BRS+ESI flag combinations, the full canonical CAN-FD DLC set
/// (0, 4, 8, 12, 16, 20, 24, 32, 48, 64) and the extended-ID path. Comparison
/// goes through
/// <see cref="TsharkAssert.AssertEquivalentMany(Stack, Packet, byte[], int, (string, string)[])"/>.
/// </para>
/// <para>Thread safety: stateless tests; no shared mutable state.</para>
/// </remarks>
internal sealed class CanFdTsharkTests
{
    private const int DltCanSocketcan = 227;

    #region Frame builders

    private static byte[] BuildFdFrame(uint canId, ReadOnlySpan<byte> data, bool extended = false, bool brs = false, bool esi = false)
    {
        SocketCanFdLayer canFd = new(canId, data, extended: extended, brs: brs, errorStateIndicator: esi);
        return FrameStack.Start(canFd).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    #endregion

    #region DLC sweep

    /// <summary>
    /// Sweeps every canonical CAN-FD payload size that maps to a unique
    /// length code; the dissector publishes <c>can.len</c> as the byte count.
    /// </summary>
    [Test]
    [Arguments(0)]
    [Arguments(4)]
    [Arguments(8)]
    [Arguments(12)]
    [Arguments(16)]
    [Arguments(20)]
    [Arguments(24)]
    [Arguments(32)]
    [Arguments(48)]
    [Arguments(64)]
    public async Task CanFd_AllCanonicalLengths_MatchTshark(int payloadLength)
    {
        byte[] data = new byte[payloadLength];
        for (int i = 0; i < payloadLength; i++)
        {
            data[i] = (byte)(0x80 + i);
        }
        byte[] frame = BuildFdFrame(0x123u, data);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.CanSocketcan);
        using (stack)
        {
            // FDF must be set by construction; BRS and ESI default to false.
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame, DltCanSocketcan,
                ("can.id", "can.id"),
                ("can.len", "can.len"),
                ("can.flags.fd", "canfd.flags.fdf"),
                ("can.flags.brs", "canfd.flags.brs"),
                ("can.flags.esi", "canfd.flags.esi")).ConfigureAwait(false);
        }
    }

    #endregion

    #region Flag matrix

    /// <summary>BRS=true, ESI=false.</summary>
    [Test]
    public async Task CanFd_BrsOnly_FlagFieldsMatchTshark()
    {
        byte[] frame = BuildFdFrame(0x456u, new byte[16], brs: true);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.CanSocketcan);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame, DltCanSocketcan,
                ("can.id", "can.id"),
                ("can.flags.fd", "canfd.flags.fdf"),
                ("can.flags.brs", "canfd.flags.brs"),
                ("can.flags.esi", "canfd.flags.esi")).ConfigureAwait(false);
        }
    }

    /// <summary>ESI=true, BRS=false.</summary>
    [Test]
    public async Task CanFd_EsiOnly_FlagFieldsMatchTshark()
    {
        byte[] frame = BuildFdFrame(0x456u, new byte[16], esi: true);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.CanSocketcan);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame, DltCanSocketcan,
                ("can.flags.fd", "canfd.flags.fdf"),
                ("can.flags.brs", "canfd.flags.brs"),
                ("can.flags.esi", "canfd.flags.esi")).ConfigureAwait(false);
        }
    }

    /// <summary>BRS=true, ESI=true.</summary>
    [Test]
    public async Task CanFd_BrsAndEsi_FlagFieldsMatchTshark()
    {
        byte[] frame = BuildFdFrame(0x456u, new byte[16], brs: true, esi: true);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.CanSocketcan);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame, DltCanSocketcan,
                ("can.id", "can.id"),
                ("can.len", "can.len"),
                ("can.flags.fd", "canfd.flags.fdf"),
                ("can.flags.brs", "canfd.flags.brs"),
                ("can.flags.esi", "canfd.flags.esi")).ConfigureAwait(false);
        }
    }

    #endregion

    #region Extended-ID

    [Test]
    public async Task CanFd_ExtendedId_FieldsMatchTshark()
    {
        byte[] frame = BuildFdFrame(0x1ABCDEFu, [0x01, 0x02, 0x03, 0x04], extended: true);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.CanSocketcan);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame, DltCanSocketcan,
                ("can.id", "can.id"),
                ("can.flags.xtd", "can.flags.xtd"),
                ("can.flags.fd", "canfd.flags.fdf"),
                ("can.len", "can.len")).ConfigureAwait(false);
        }
    }

    #endregion
}
