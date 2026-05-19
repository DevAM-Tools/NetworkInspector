// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// LIN happy-path tests. Validates PID, parity, length, data and checksum
/// fields for both classic and enhanced checksum modes.
/// </summary>
internal sealed class LinBasicTests
{
    [Test]
    public async Task Parse_EnhancedChecksum()
    {
        byte[] frame = FrameStack.Start(new LinLayer(0x10, [0x01, 0x02, 0x03, 0x04], checksumType: 2)).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.Lin);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "lin.id", 0x10).ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "lin.length", 4).ConfigureAwait(false);
            await ProtocolTestHelper.AssertStringField(stack, packet, "lin.checksum_type", "Enhanced").ConfigureAwait(false);
            await ProtocolTestHelper.AssertBoolField(stack, packet, "lin.parity.valid", true).ConfigureAwait(false);
            await ProtocolTestHelper.AssertStringField(stack, packet, "lin.checksum.status", "[Good]").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_ClassicChecksum()
    {
        byte[] frame = FrameStack.Start(new LinLayer(0x3C, [0xAA, 0xBB], checksumType: 1)).CreateWithFixedValues().EmitFrame([]);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.Lin);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "lin.id", 0x3C).ConfigureAwait(false);
            await ProtocolTestHelper.AssertStringField(stack, packet, "lin.checksum_type", "Classic").ConfigureAwait(false);
            await ProtocolTestHelper.AssertStringField(stack, packet, "lin.checksum.status", "[Good]").ConfigureAwait(false);
        }
    }
}
