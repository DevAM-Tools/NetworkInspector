// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// CAN classic and CAN FD happy-path tests via the SocketCAN link-type.
/// Frames are built with the production <see cref="FrameStack"/> API so the
/// tests exercise the same code path used by user-facing builders.
/// </summary>
internal sealed class CanBasicTests
{
    private static byte[] BuildClassic(uint canId, ReadOnlySpan<byte> data, bool extended = false, bool rtr = false)
    {
        SocketCanLayer can = new(canId, data, extended: extended, remoteTransmissionRequest: rtr);
        return FrameStack.Start(can).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    private static byte[] BuildFd(uint canId, ReadOnlySpan<byte> data, bool brs = false, bool esi = false)
    {
        SocketCanFdLayer canFd = new(canId, data, brs: brs, errorStateIndicator: esi);
        return FrameStack.Start(canFd).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    [Test]
    public async Task Parse_ClassicStandardId()
    {
        byte[] frame = BuildClassic(0x123u, [0xDE, 0xAD, 0xBE, 0xEF]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.CanSocketcan);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "can.id", 0x123).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "can.len", 4).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "can.flags.xtd", false).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "can.flags.rtr", false).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_ClassicExtendedId()
    {
        byte[] frame = BuildClassic(0x1ABCDEFu, [], extended: true);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.CanSocketcan);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "can.id", 0x1ABCDEF).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "can.flags.xtd", true).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_RemoteTransmissionRequest()
    {
        byte[] frame = BuildClassic(0x100u, [], rtr: true);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.CanSocketcan);
        using (stack)
        {
            await ProtocolTestHelper.AssertBoolField(stack, packet, "can.flags.rtr", true).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_FdFrame_BrsAndEsi()
    {
        byte[] frame = BuildFd(0x456u, new byte[16], brs: true, esi: true);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.CanSocketcan);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "can.id", 0x456).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "can.len", 16).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "can.flags.fd", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "can.flags.brs", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "can.flags.esi", true).ConfigureAwait(false);
        }
    }

    #region Flags container display text

    [Test]
    public async Task Parse_ClassicFlags_DisplayText_NoFlags()
    {
        byte[] frame = BuildClassic(0x123u, [0xDE, 0xAD]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.CanSocketcan);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "can.flags", "[None]").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_ClassicFlags_DisplayText_Extended()
    {
        byte[] frame = BuildClassic(0x1ABCDEFu, [], extended: true);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.CanSocketcan);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "can.flags", "[XTD]").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_FdFrame_DisplayText_BrsAndEsi()
    {
        byte[] frame = BuildFd(0x456u, new byte[8], brs: true, esi: true);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.CanSocketcan);
        using (stack)
        {
            // FD frames always have "FD" as the first token.
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "can.flags", "[FD, BRS, ESI]").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_FdFrame_DisplayText_NoAdditionalFlags()
    {
        byte[] frame = BuildFd(0x100u, new byte[8]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.CanSocketcan);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "can.flags", "[FD]").ConfigureAwait(false);
        }
    }

    #endregion
}
