// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for SLL, SLL2, and LLC protocol truncation handling.
/// Verifies that each parser gracefully rejects frames that are too short
/// to contain the minimum required header, without panicking or producing
/// partial / incorrect fields.
/// </summary>
internal sealed class LinkLayerTruncationTests
{
    #region SLL (Linux Cooked Capture v1) — HeaderSize = 16 bytes

    [Test]
    public async Task SLL_TruncatedFrame_OneByte_NoFields()
    {
        // SLL requires 16 bytes; provide only 1
        byte[] frame = [0x00];

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame, LinkType.LinuxSll);
        Stack stack = ProtocolTestHelper.SharedStack;

        FieldId? id = stack.GetFieldId("sll.pkttype");
        if (id.HasValue)
        {
            bool found = packet.TryGetFieldValue(id.Value, out _);
            await Assert.That(found).IsFalse()
                .Because("SLL: truncated frame (1 byte) must not produce sll.pkttype");
        }
        else
        {
            return; // field not registered → acceptable
        }
    }

    [Test]
    public async Task SLL_TruncatedFrame_FifteenBytes_NoFields()
    {
        // SLL requires 16 bytes; provide exactly 15 (one short)
        byte[] frame = new byte[15];

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame, LinkType.LinuxSll);
        Stack stack = ProtocolTestHelper.SharedStack;

        FieldId? id = stack.GetFieldId("sll.pkttype");
        if (id.HasValue)
        {
            bool found = packet.TryGetFieldValue(id.Value, out _);
            await Assert.That(found).IsFalse()
                .Because("SLL: 15-byte frame (one short of header) must not produce sll.pkttype");
        }
    }

    [Test]
    public async Task SLL_ValidFrame_SixteenBytes_PktTypePresent()
    {
        // Minimum valid SLL frame (exactly 16-byte header, no payload)
        // pkttype = 0 (Unicast), hatype = 1 (Ethernet), halen = 6, src MAC = zeros, etype = 0x0000
        byte[] frame = new byte[16];
        Span<byte> s = frame;
        BinaryPrimitives.WriteUInt16BigEndian(s, 0);       // pkttype = 0
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], 1);  // hatype = 1 (Ethernet)
        BinaryPrimitives.WriteUInt16BigEndian(s[4..], 6);  // halen = 6
        // src MAC = 6 bytes of zeros (s[6..12])
        BinaryPrimitives.WriteUInt16BigEndian(s[14..], 0); // etype = 0x0000

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.LinuxSll);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "sll.pkttype", 0).ConfigureAwait(false);
        }
    }

    #endregion

    #region SLL2 (Linux Cooked Capture v2) — HeaderSize = 20 bytes

    [Test]
    public async Task SLL2_TruncatedFrame_OneByte_NoFields()
    {
        byte[] frame = [0x00];

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame, LinkType.LinuxSll2);
        Stack stack = ProtocolTestHelper.SharedStack;

        FieldId? id = stack.GetFieldId("sll2.pkttype");
        if (id.HasValue)
        {
            bool found = packet.TryGetFieldValue(id.Value, out _);
            await Assert.That(found).IsFalse()
                .Because("SLL2: truncated frame (1 byte) must not produce sll2.pkttype");
        }
    }

    [Test]
    public async Task SLL2_TruncatedFrame_NineteenBytes_NoFields()
    {
        // SLL2 requires 20 bytes; provide exactly 19 (one short)
        byte[] frame = new byte[19];

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame, LinkType.LinuxSll2);
        Stack stack = ProtocolTestHelper.SharedStack;

        FieldId? id = stack.GetFieldId("sll2.pkttype");
        if (id.HasValue)
        {
            bool found = packet.TryGetFieldValue(id.Value, out _);
            await Assert.That(found).IsFalse()
                .Because("SLL2: 19-byte frame (one short of header) must not produce sll2.pkttype");
        }
    }

    [Test]
    public async Task SLL2_ValidFrame_TwentyBytes_PktTypePresent()
    {
        // Minimum valid SLL2 frame (exactly 20-byte header, no payload)
        // etype=0x0000, reserved=0, if_index=0, hatype=1, pkttype=0, halen=6, src=zeros
        byte[] frame = new byte[20];
        Span<byte> s = frame;
        BinaryPrimitives.WriteUInt16BigEndian(s, 0);        // etype = 0x0000
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], 0);   // reserved = 0
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], 0);   // if_index = 0
        BinaryPrimitives.WriteUInt16BigEndian(s[8..], 1);   // hatype = 1 (Ethernet)
        s[10] = 0;                                           // pkttype = 0
        s[11] = 6;                                           // halen = 6
        // src MAC = 8 bytes (s[12..20]) — zeros

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.LinuxSll2);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "sll2.pkttype", 0).ConfigureAwait(false);
        }
    }

    #endregion

    #region LLC — MinHeaderSize = 3 bytes (DSAP + SSAP + Control)

    [Test]
    public async Task LLC_TruncatedFrame_ZeroLlcBytes_NoFields()
    {
        // Build an IEEE 802.3 frame with EtherType < 0x0600 (triggers LLC dispatch)
        // but provide zero bytes for the LLC payload (too short for 3-byte minimum)
        byte[] frame = BuildIeee8023Frame(etherLen: 0, llcPayload: ReadOnlySpan<byte>.Empty);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        FieldId? id = stack.GetFieldId("llc.dsap");
        if (id.HasValue)
        {
            bool found = packet.TryGetFieldValue(id.Value, out _);
            await Assert.That(found).IsFalse()
                .Because("LLC: empty payload must not produce llc.dsap");
        }
    }

    [Test]
    public async Task LLC_TruncatedFrame_TwoLlcBytes_NoFields()
    {
        // Two LLC bytes — one short of the 3-byte minimum (DSAP + SSAP only, no Control)
        byte[] llcPayload = [0xAA, 0xAA]; // DSAP = SNAP, SSAP = SNAP, missing Control byte
        byte[] frame = BuildIeee8023Frame(etherLen: (ushort)llcPayload.Length, llcPayload: llcPayload);

        Packet packet = ProtocolTestHelper.ParseWithSharedStack(frame);
        Stack stack = ProtocolTestHelper.SharedStack;

        FieldId? id = stack.GetFieldId("llc.dsap");
        if (id.HasValue)
        {
            bool found = packet.TryGetFieldValue(id.Value, out _);
            await Assert.That(found).IsFalse()
                .Because("LLC: 2-byte payload (missing Control) must not produce llc.dsap");
        }
    }

    [Test]
    public async Task LLC_ValidFrame_ThreeLlcBytes_DsapPresent()
    {
        // Minimum valid LLC header: DSAP=0x42, SSAP=0x42, Control=0x03 (Spanning Tree)
        byte[] llcPayload = [0x42, 0x42, 0x03];
        byte[] frame = BuildIeee8023Frame(etherLen: (ushort)llcPayload.Length, llcPayload: llcPayload);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "llc.dsap", 0x42).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds a raw IEEE 802.3 Ethernet frame (EtherType field = length value below 0x0600)
    /// so the Ethernet parser dispatches to LLC.
    /// Layout: dst(6) + src(6) + length(2) + llcPayload.
    /// </summary>
    private static byte[] BuildIeee8023Frame(ushort etherLen, ReadOnlySpan<byte> llcPayload)
    {
        byte[] frame = new byte[14 + llcPayload.Length];
        Span<byte> s = frame;

        // dst MAC
        s[0] = 0x00;
        s[1] = 0x11;
        s[2] = 0x22;
        s[3] = 0x33;
        s[4] = 0x44;
        s[5] = 0x55;
        // src MAC
        s[6] = 0x66;
        s[7] = 0x77;
        s[8] = 0x88;
        s[9] = 0x99;
        s[10] = 0xAA;
        s[11] = 0xBB;
        // length field (must be < 0x0600 to be treated as IEEE 802.3)
        BinaryPrimitives.WriteUInt16BigEndian(s[12..], etherLen);

        if (llcPayload.Length > 0)
        {
            llcPayload.CopyTo(s[14..]);
        }

        return frame;
    }

    #endregion
}
