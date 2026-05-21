// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Symmetric tshark cross-validation for the SocketCAN classic dissector
/// (Plan §3.1.6). Pcap link type 227 (LINKTYPE_CAN_SOCKETCAN). Frames are
/// emitted via <see cref="SocketCanLayer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Coverage: standard 11-bit IDs across the full DLC range 0..8, the 29-bit
/// extended-ID path (EFF flag), the RTR flag, the ERR (error frame) flag.
/// Comparison goes through
/// <see cref="TsharkAssert.AssertEquivalentMany(Stack, Packet, byte[], int, (string, string)[])"/>.
/// </para>
/// <para>Thread safety: stateless tests; no shared mutable state.</para>
/// </remarks>
internal sealed class CanTsharkTests
{
    private const int DltCanSocketcan = 227;

    #region Frame builders

    /// <summary>Standard-ID classic CAN frame with caller-supplied data.</summary>
    private static byte[] BuildClassicFrame(uint canId, ReadOnlySpan<byte> data)
    {
        SocketCanLayer can = new(canId, data);
        return FrameStack.Start(can).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    private static byte[] BuildExtendedFrame(uint canId, ReadOnlySpan<byte> data)
    {
        SocketCanLayer can = new(canId, data, extended: true);
        return FrameStack.Start(can).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    private static byte[] BuildRemoteFrame(uint canId)
    {
        SocketCanLayer can = new(canId, data: ReadOnlySpan<byte>.Empty, remoteTransmissionRequest: true);
        return FrameStack.Start(can).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    private static byte[] BuildErrorFrame(uint canId)
    {
        SocketCanLayer can = new(canId, data: ReadOnlySpan<byte>.Empty, errorFrame: true);
        return FrameStack.Start(can).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    #endregion

    #region Standard-ID + DLC sweep

    /// <summary>
    /// Sweeps the full classic CAN DLC range (0..8). Each value is its own
    /// frame so a regression at a specific DLC stays pinpointable.
    /// </summary>
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    [Arguments(8)]
    public async Task Can_StandardId_AllDlcSizes_MatchTshark(int dlc)
    {
        byte[] data = new byte[dlc];
        for (int i = 0; i < dlc; i++)
        {
            data[i] = (byte)(0x10 + i);
        }
        byte[] frame = BuildClassicFrame(0x123u, data);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.CanSocketcan);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame, DltCanSocketcan,
                ("can.id", "can.id"),
                ("can.len", "can.len"),
                ("can.flags.xtd", "can.flags.xtd"),
                ("can.flags.rtr", "can.flags.rtr")).ConfigureAwait(false);
        }
    }

    #endregion

    #region Extended ID

    /// <summary>29-bit extended identifier (EFF). Pins XTD flag and id wire encoding.</summary>
    [Test]
    public async Task Can_ExtendedId_FieldsMatchTshark()
    {
        byte[] frame = BuildExtendedFrame(0x1ABCDEFu, [0x42]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.CanSocketcan);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame, DltCanSocketcan,
                ("can.id", "can.id"),
                ("can.flags.xtd", "can.flags.xtd"),
                ("can.len", "can.len")).ConfigureAwait(false);
        }
    }

    #endregion

    #region Remote frame

    /// <summary>RTR flag set, no payload. Pins the RTR bit.</summary>
    [Test]
    public async Task Can_RemoteTransmissionRequest_FieldsMatchTshark()
    {
        byte[] frame = BuildRemoteFrame(0x321u);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.CanSocketcan);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame, DltCanSocketcan,
                ("can.id", "can.id"),
                ("can.flags.rtr", "can.flags.rtr"),
                ("can.flags.xtd", "can.flags.xtd")).ConfigureAwait(false);
        }
    }

    #endregion

    #region Error frame

    /// <summary>
    /// ERR flag set: tshark renders an error message frame; we pin the
    /// fields that remain symmetric across both decoders.
    /// </summary>
    [Test]
    public async Task Can_ErrorFrame_FieldsMatchTshark()
    {
        byte[] frame = BuildErrorFrame(0x456u);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.CanSocketcan);
        using (stack)
        {
            await TsharkAssert.AssertEquivalentMany(stack, packet, frame, DltCanSocketcan,
                ("can.len", "can.len")).ConfigureAwait(false);
        }
    }

    #endregion
}
