// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Round-trip / NI-only validation for the SocketCAN-XL dissector
/// (Plan §3.1.6). Frames are emitted via <see cref="SocketCanXlLayer"/>.
/// </summary>
/// <remarks>
/// <para>
/// The Wireshark CAN dissector at link type 227 (LINKTYPE_CAN_SOCKETCAN)
/// does not surface CAN-XL frames as the <c>canxl.*</c> protocol family —
/// XL support requires the dedicated DLT_CAN_XL link type which is not
/// emitted by the in-memory pcap-ng writer. Until that path exists, this
/// suite validates the NI parser's own field tree (canxl.flags.xlf,
/// canxl.flags.sec, canxl.len) against the values that were emitted, which
/// keeps the smoke coverage high without producing false-positive failures
/// from missing tshark fields.
/// </para>
/// <para>Thread safety: stateless tests; no shared mutable state.</para>
/// </remarks>
internal sealed class CanXlTsharkTests
{
    #region Frame builders

    private static byte[] _BuildXlFrame(uint priority, ReadOnlyMemory<byte> data, byte sdt = 0, uint af = 0, bool sec = false)
    {
        SocketCanXlLayer xl = new(priority, data, sdt: sdt, af: af, sec: sec);
        return FrameStack.Start(xl).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    private static byte[] _CreatePayload(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)(i & 0xFF);
        }
        return data;
    }

    #endregion

    #region Length sweep

    /// <summary>
    /// Three representative payload sizes — small (1), medium (128), and
    /// near-max (2048). Pins length encoding and core dissector field set.
    /// </summary>
    [Test]
    [Arguments(1)]
    [Arguments(128)]
    [Arguments(2048)]
    public async Task CanXl_VariousLengths_RoundTrip(int payloadLength)
    {
        byte[] frame = _BuildXlFrame(0x12345u, _CreatePayload(payloadLength));
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.CanSocketcan);
        using (stack)
        {
            await ProtocolTestHelper.AssertBoolField(stack, packet, "canxl.flags.xlf", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "canxl.flags.sec", false).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "canxl.len", (ulong)payloadLength).ConfigureAwait(false);
        }
    }

    #endregion

    #region SEC flag

    [Test]
    public async Task CanXl_SecFlag_RoundTrip()
    {
        byte[] frame = _BuildXlFrame(0x100u, _CreatePayload(64), sec: true);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.CanSocketcan);
        using (stack)
        {
            await ProtocolTestHelper.AssertBoolField(stack, packet, "canxl.flags.xlf", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "canxl.flags.sec", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "canxl.len", 64).ConfigureAwait(false);
        }
    }

    #endregion

    #region SDT + AF

    /// <summary>Non-default SDU-Type and Acceptance-Field; pins both wire fields.</summary>
    [Test]
    public async Task CanXl_SduTypeAndAcceptanceField_RoundTrip()
    {
        byte[] frame = _BuildXlFrame(0x200u, _CreatePayload(32), sdt: 0x03, af: 0x12345678u);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.CanSocketcan);
        using (stack)
        {
            await ProtocolTestHelper.AssertBoolField(stack, packet, "canxl.flags.xlf", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "canxl.sdu_type", 0x03).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "canxl.acceptance_field", 0x12345678u).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "canxl.len", 32).ConfigureAwait(false);
        }
    }

    #endregion

    #region Flags container display text

    [Test]
    public async Task CanXl_FlagsDisplayText_XlfOnly()
    {
        // XLF is structurally always set; no optional SEC or RRS flag set.
        byte[] frame = _BuildXlFrame(0x100u, _CreatePayload(8));
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.CanSocketcan);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "canxl.flags", "[XLF]").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task CanXl_FlagsDisplayText_XlfAndSec()
    {
        // SEC flag set in addition to the structural XLF.
        byte[] frame = _BuildXlFrame(0x100u, _CreatePayload(8), sec: true);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.CanSocketcan);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "canxl.flags", "[XLF, SEC]").ConfigureAwait(false);
        }
    }

    #endregion
}
