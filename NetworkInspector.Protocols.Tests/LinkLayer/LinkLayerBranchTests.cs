// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Branch-coverage tests for SLL (Linux Cooked Capture v1), SLL2 (v2), and LLC+SNAP parsers.
/// Each test targets a specific conditional path that is not exercised by the existing happy-path
/// suite in <c>NetworkInspector.Core.Tests/LinkLayerProtocolTests.cs</c>.
/// <para>
/// Branches under test:
/// <list type="bullet">
/// <item>SLL/SLL2 <c>haLen &lt; 6</c>: source MAC defaults to <c>00:00:00:00:00:00</c>.</item>
/// <item>SLL/SLL2 <c>etherType &lt;= 0x0600</c>: EtherType dispatch is skipped entirely.</item>
/// <item>LLC I/S-frame: 2-byte control field (vs. 1-byte for U-frames).</item>
/// <item>LLC SNAP with non-zero OUI: EtherType dispatch is skipped (vendor-specific).</item>
/// <item>LLC non-SNAP DSAP with I/G bit set: raw value stored, masked key used for dispatch.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class LinkLayerBranchTests
{
    #region Frame construction helpers

    /// <summary>
    /// Builds a minimal 16-byte SLL v1 frame. No payload is appended, which is intentional:
    /// the tests only assert on SLL header fields or protocol-dispatch absence.
    /// </summary>
    private static byte[] BuildSllFrame(ushort haLen, ushort etherType, byte[] addressBytes)
    {
        byte[] frame = new byte[16]; // SLL header size
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0), 0);          // pktType = 0 (unicast)
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 1);          // haType  = 1 (Ethernet)
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), haLen);      // address length
        // Address field: 8 bytes at offset 6; addressBytes may be shorter
        int copyLen = Math.Min(addressBytes.Length, 8);
        addressBytes.AsSpan(0, copyLen).CopyTo(frame.AsSpan(6, copyLen));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(14), etherType); // EtherType / next proto
        return frame;
    }

    /// <summary>
    /// Builds a minimal 20-byte SLL2 frame with no payload.
    /// </summary>
    private static byte[] BuildSll2Frame(byte haLen, ushort etherType, byte[] addressBytes)
    {
        byte[] frame = new byte[20]; // SLL2 header size
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0), etherType); // protocol
        // [2:3] reserved = 0
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), 1);         // ifIndex = 1
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8), 1);         // haType  = 1 (Ethernet)
        frame[10] = 0;                                                       // pktType = 0 (unicast)
        frame[11] = haLen;                                                   // address length
        // Address field: 8 bytes at offset 12
        int copyLen = Math.Min(addressBytes.Length, 8);
        addressBytes.AsSpan(0, copyLen).CopyTo(frame.AsSpan(12, copyLen));
        return frame;
    }

    /// <summary>
    /// Builds an Ethernet 802.3 frame with an LLC U-frame or I/S-frame header.
    /// The Ethernet length field (bytes 12–13) is set to the LLC header size.
    /// </summary>
    private static byte[] BuildEthernetLlcFrame(byte dsap, byte ssap, byte control1, byte? control2)
    {
        // LLC header: 3 bytes for U-frame, 4 bytes for I/S-frame
        int llcSize = control2.HasValue ? 4 : 3;
        byte[] frame = new byte[14 + llcSize];

        // Ethernet header — no real MACs needed; any values work
        frame[0] = 0x00; frame[1] = 0x11; frame[2] = 0x22;
        frame[3] = 0x33; frame[4] = 0x44; frame[5] = 0x55; // dst MAC
        frame[6] = 0x66; frame[7] = 0x77; frame[8] = 0x88;
        frame[9] = 0x99; frame[10] = 0xAA; frame[11] = 0xBB; // src MAC
        // Length field (≤ 1500 = 0x05DC triggers 802.3 / LLC path)
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), (ushort)llcSize);

        // LLC header
        frame[14] = dsap;
        frame[15] = ssap;
        frame[16] = control1;
        if (control2.HasValue)
        {
            frame[17] = control2.Value;
        }

        return frame;
    }

    /// <summary>
    /// Builds an Ethernet 802.3 frame with an LLC SNAP header.
    /// </summary>
    private static byte[] BuildEthernetLlcSnapFrame(byte oui0, byte oui1, byte oui2, ushort snapType)
    {
        // LLC (3) + SNAP (5) = 8 bytes
        const int llcSnapSize = 8;
        byte[] frame = new byte[14 + llcSnapSize];

        // Ethernet header
        frame[0] = 0x00; frame[1] = 0x11; frame[2] = 0x22;
        frame[3] = 0x33; frame[4] = 0x44; frame[5] = 0x55; // dst MAC
        frame[6] = 0x66; frame[7] = 0x77; frame[8] = 0x88;
        frame[9] = 0x99; frame[10] = 0xAA; frame[11] = 0xBB; // src MAC
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), (ushort)llcSnapSize); // length

        // LLC: DSAP=0xAA, SSAP=0xAA, Control=0x03 (UI — required for SNAP)
        frame[14] = 0xAA;
        frame[15] = 0xAA;
        frame[16] = 0x03;

        // SNAP: OUI (3 bytes) + Type (2 bytes)
        frame[17] = oui0;
        frame[18] = oui1;
        frame[19] = oui2;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(20), snapType);

        return frame;
    }

    /// <summary>
    /// Builds an IEEE 802.3 + LLC SNAP + IPv4 frame with zero OUI (00:00:00) and EtherType 0x0800.
    /// Used to verify that the zero-OUI SNAP path dispatches to IPv4.
    /// </summary>
    private static byte[] BuildLlcSnapIpv4Frame()
    {
        const int ethSize = 14;
        const int llcSnapSize = 8; // LLC(3) + SNAP(5)
        const int ipv4Size = 20;
        byte[] frame = new byte[ethSize + llcSnapSize + ipv4Size];

        // Ethernet header: length field (≤ 1500) triggers 802.3/LLC path
        frame[0] = 0x00; frame[1] = 0x11; frame[2] = 0x22;
        frame[3] = 0x33; frame[4] = 0x44; frame[5] = 0x55; // dst MAC
        frame[6] = 0x66; frame[7] = 0x77; frame[8] = 0x88;
        frame[9] = 0x99; frame[10] = 0xAA; frame[11] = 0xBB; // src MAC
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), (ushort)(llcSnapSize + ipv4Size));

        // LLC: DSAP=0xAA, SSAP=0xAA, Control=0x03 (UI — SNAP indicator)
        frame[14] = 0xAA;
        frame[15] = 0xAA;
        frame[16] = 0x03;

        // SNAP: OUI=00:00:00 (zero OUI triggers EtherType dispatch), Type=0x0800 (IPv4)
        // OUI bytes at [17..19] are already zero from array initialization
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(20), 0x0800);

        // Minimal IPv4 header: version=4, IHL=5, total length = ipv4Size (no payload)
        int ipOffset = ethSize + llcSnapSize;
        frame[ipOffset] = 0x45;                                          // Version=4, IHL=5
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(ipOffset + 2), ipv4Size); // total length
        frame[ipOffset + 8] = 64;                                        // TTL
        frame[ipOffset + 9] = 17;                                        // Protocol = UDP
        frame[ipOffset + 12] = 10; frame[ipOffset + 13] = 0;
        frame[ipOffset + 14] = 0; frame[ipOffset + 15] = 1;             // src IP: 10.0.0.1
        frame[ipOffset + 16] = 10; frame[ipOffset + 17] = 0;
        frame[ipOffset + 18] = 0; frame[ipOffset + 19] = 2;             // dst IP: 10.0.0.2
        return frame;
    }

    #endregion

    // === SLL: haLen < 6 → default (zero) source MAC ===

    [Test]
    public async Task Parse_SllFrame_HaLenZero_SrcMacIsAllZeros()
    {
        // haLen = 0 means no meaningful address bytes → srcMac falls to default (00:00:00:00:00:00)
        // The address bytes are set to non-zero to confirm they are ignored.
        byte[] frame = BuildSllFrame(haLen: 0, etherType: 0x0000,
            addressBytes: [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00, 0x00]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.LinuxSll);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "sll.halen", 0UL).ConfigureAwait(false);
            await ProtocolTestHelper.AssertMacField(stack, packet, "sll.src.eth", "00:00:00:00:00:00").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_SllFrame_HaLenFive_SrcMacIsAllZeros()
    {
        // haLen = 5 (< 6) — still below the threshold; address bytes are non-zero but must be ignored
        byte[] frame = BuildSllFrame(haLen: 5, etherType: 0x0000,
            addressBytes: [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.LinuxSll);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "sll.halen", 5UL).ConfigureAwait(false);
            await ProtocolTestHelper.AssertMacField(stack, packet, "sll.src.eth", "00:00:00:00:00:00").ConfigureAwait(false);
        }
    }

    // === SLL: etherType <= 0x0600 → no protocol dispatch ===

    [Test]
    public async Task Parse_SllFrame_EtherTypeAtThreshold0x0600_NoIpv4Dispatch()
    {
        // etherType = 0x0600 is NOT strictly greater than MinEtherType (0x0600), so dispatch is skipped
        byte[] frame = BuildSllFrame(haLen: 6, etherType: 0x0600,
            addressBytes: [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00, 0x00]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.LinuxSll);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "sll.etype", 0x0600UL).ConfigureAwait(false);
            // No IPv4 dispatch must occur: ip.src must be absent
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "ip.src").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_SllFrame_EtherTypeBelowThreshold0x0001_NoIpv4Dispatch()
    {
        // etherType = 0x0001 is well below 0x0600 — definitely no dispatch
        byte[] frame = BuildSllFrame(haLen: 6, etherType: 0x0001,
            addressBytes: [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00, 0x00]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.LinuxSll);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "sll.etype", 0x0001UL).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "ip.src").ConfigureAwait(false);
        }
    }

    // === SLL2: haLen < 6 → default (zero) source MAC ===

    [Test]
    public async Task Parse_Sll2Frame_HaLenZero_SrcMacIsAllZeros()
    {
        byte[] frame = BuildSll2Frame(haLen: 0, etherType: 0x0000,
            addressBytes: [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00, 0x00]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.LinuxSll2);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "sll2.halen", 0UL).ConfigureAwait(false);
            await ProtocolTestHelper.AssertMacField(stack, packet, "sll2.src.eth", "00:00:00:00:00:00").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Sll2Frame_HaLenFive_SrcMacIsAllZeros()
    {
        byte[] frame = BuildSll2Frame(haLen: 5, etherType: 0x0000,
            addressBytes: [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.LinuxSll2);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "sll2.halen", 5UL).ConfigureAwait(false);
            await ProtocolTestHelper.AssertMacField(stack, packet, "sll2.src.eth", "00:00:00:00:00:00").ConfigureAwait(false);
        }
    }

    // === SLL2: etherType <= 0x0600 → no protocol dispatch ===

    [Test]
    public async Task Parse_Sll2Frame_EtherTypeAtThreshold0x0600_NoIpv4Dispatch()
    {
        byte[] frame = BuildSll2Frame(haLen: 6, etherType: 0x0600,
            addressBytes: [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00, 0x00]);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.LinuxSll2);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "sll2.etype", 0x0600UL).ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "ip.src").ConfigureAwait(false);
        }
    }

    // === LLC I-frame: 2-byte control (bit 0 of Control = 0) ===

    [Test]
    public async Task Parse_LlcIFrame_ControlFieldIsReadAsTwoByteLittleEndian()
    {
        // Control1 = 0x00 (bit 0 = 0 → I-frame), Control2 = 0x01
        // controlValue = LE(0x00, 0x01) = 0x0100 = 256
        byte[] frame = BuildEthernetLlcFrame(dsap: 0x04, ssap: 0x04, control1: 0x00, control2: 0x01);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "llc").ConfigureAwait(false);
            await ProtocolTestHelper.AssertU64Field(stack, packet, "llc.dsap", 0x04UL).ConfigureAwait(false);
            // 2-byte LE control: 0x00 | (0x01 << 8) = 256
            await ProtocolTestHelper.AssertU64Field(stack, packet, "llc.control", 256UL).ConfigureAwait(false);
        }
    }

    // === LLC S-frame: 2-byte control (bits [1:0] of Control = 01) ===

    [Test]
    public async Task Parse_LlcSFrame_ControlFieldIsReadAsTwoByteLittleEndian()
    {
        // Control1 = 0x01 (bits [1:0] = 01 → S-frame), Control2 = 0x00
        // controlValue = LE(0x01, 0x00) = 0x0001 = 1
        byte[] frame = BuildEthernetLlcFrame(dsap: 0x04, ssap: 0x04, control1: 0x01, control2: 0x00);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "llc").ConfigureAwait(false);
            // 2-byte LE control: 0x01 | (0x00 << 8) = 1
            await ProtocolTestHelper.AssertU64Field(stack, packet, "llc.control", 1UL).ConfigureAwait(false);
        }
    }

    // === LLC SNAP non-zero OUI: EtherType dispatch skipped ===

    [Test]
    public async Task Parse_LlcSnapNonZeroOui_NoIpv4Dispatch()
    {
        // OUI = 00:00:01 (non-zero) → vendor-specific, no EtherType dispatch even with Type=0x0800
        byte[] frame = BuildEthernetLlcSnapFrame(oui0: 0x00, oui1: 0x00, oui2: 0x01, snapType: 0x0800);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "llc").ConfigureAwait(false);
            // Type field is present and set to 0x0800
            await ProtocolTestHelper.AssertU64Field(stack, packet, "llc.type", 0x0800UL).ConfigureAwait(false);
            // But IPv4 must NOT be dispatched because OUI is non-zero
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "ip.src").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_LlcSnapZeroOui_IpV4IsDispatched()
    {
        // Zero OUI (00:00:00) + Type=0x0800 MUST dispatch to IPv4.
        // This is the complement to the non-zero OUI test, confirming the branch is taken.
        byte[] frame = BuildLlcSnapIpv4Frame();

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "llc").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "ip.src").ConfigureAwait(false);
        }
    }

    // === LLC non-SNAP DSAP: raw value stored, I/G bit masked for dispatch ===

    [Test]
    public async Task Parse_LlcNonSnap_DsapIgBitSet_RawValueStoredInField()
    {
        // DSAP = 0x01 (I/G bit set; raw value differs from dispatch key 0x01 & 0xFE = 0x00).
        // The field must store the raw, unmasked DSAP value 0x01.
        byte[] frame = BuildEthernetLlcFrame(dsap: 0x01, ssap: 0x00, control1: 0x03, control2: null);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "llc").ConfigureAwait(false);
            // Field must contain the raw DSAP value, not the masked dispatch key
            await ProtocolTestHelper.AssertU64Field(stack, packet, "llc.dsap", 0x01UL).ConfigureAwait(false);
        }
    }
}
