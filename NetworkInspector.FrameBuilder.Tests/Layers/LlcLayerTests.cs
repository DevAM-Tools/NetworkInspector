// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder.Tests.Layers;

/// <summary>
/// Tests for <see cref="LlcLayer"/> — covers both plain 3-byte LLC and the 8-byte SNAP
/// extension; verifies DSAP/SSAP/Control values, OUI byte layout, and EtherType patching.
/// </summary>
internal sealed class LlcLayerTests
{
    #region HeaderSize

    [Test]
    public async Task HeaderSize_PlainLlc_Is3()
    {
        LlcLayer layer = new(dsap: 0x00, ssap: 0x00);

        await Assert.That(layer.HeaderSize).IsEqualTo(3);
    }

    [Test]
    public async Task HeaderSize_SnapLlc_Is8()
    {
        LlcLayer layer = LlcLayer.CreateSnap();

        await Assert.That(layer.HeaderSize).IsEqualTo(8);
    }

    #endregion

    #region Plain LLC header layout

    [Test]
    public async Task WriteHeader_PlainLlc_DsapSsapControlWrittenAtCorrectOffsets()
    {
        // Verify that the three LLC bytes are placed at the correct offsets.
        LlcLayer layer = new(dsap: 0xFE, ssap: 0xFE, control: 0x03);
        byte[] buf = new byte[3];

        layer.WriteHeader(buf.AsSpan());

        await Assert.That(buf[0]).IsEqualTo((byte)0xFE).Because("byte 0 is DSAP");
        await Assert.That(buf[1]).IsEqualTo((byte)0xFE).Because("byte 1 is SSAP");
        await Assert.That(buf[2]).IsEqualTo((byte)0x03).Because("byte 2 is Control");
    }

    [Test]
    public async Task WriteHeader_PlainLlc_CustomControl_StoredVerbatim()
    {
        // The control byte must be stored verbatim, not forced to 0x03.
        LlcLayer layer = new(dsap: 0x00, ssap: 0x00, control: 0xFF);
        byte[] buf = new byte[3];

        layer.WriteHeader(buf.AsSpan());

        await Assert.That(buf[2]).IsEqualTo((byte)0xFF);
    }

    #endregion

    #region SNAP LLC header layout

    [Test]
    public async Task WriteHeader_SnapLlc_DsapAndSsapAreSnapSap()
    {
        // SNAP frames always set DSAP=0xAA and SSAP=0xAA per IEEE 802.2.
        LlcLayer layer = LlcLayer.CreateSnap();
        byte[] buf = new byte[8];

        layer.WriteHeader(buf.AsSpan());

        await Assert.That(buf[0]).IsEqualTo(LlcLayer.SnapSap).Because("DSAP must be SnapSap (0xAA)");
        await Assert.That(buf[1]).IsEqualTo(LlcLayer.SnapSap).Because("SSAP must be SnapSap (0xAA)");
        await Assert.That(buf[2]).IsEqualTo(LlcLayer.SnapControl).Because("Control must be SnapControl (0x03)");
    }

    [Test]
    public async Task WriteHeader_SnapLlc_OuiWrittenBigEndianAtOffset3()
    {
        // OUI occupies bytes 3–5 in big-endian order.
        const uint oui = 0x00_60_2C; // example Cisco OUI
        LlcLayer layer = LlcLayer.CreateSnap(oui: oui);
        byte[] buf = new byte[8];

        layer.WriteHeader(buf.AsSpan());

        await Assert.That(buf[3]).IsEqualTo((byte)0x00).Because("OUI byte 0 at offset 3");
        await Assert.That(buf[4]).IsEqualTo((byte)0x60).Because("OUI byte 1 at offset 4");
        await Assert.That(buf[5]).IsEqualTo((byte)0x2C).Because("OUI byte 2 at offset 5");
    }

    [Test]
    public async Task WriteHeader_SnapLlc_OuiCappedTo24Bits()
    {
        // Bits above 23 in the OUI parameter must be discarded (& 0xFFFFFF).
        const uint oui = 0xFF_00_11_22; // upper byte must be stripped
        LlcLayer layer = LlcLayer.CreateSnap(oui: oui);
        byte[] buf = new byte[8];

        layer.WriteHeader(buf.AsSpan());

        // Only bits[23:0] = 0x00_11_22 should appear
        await Assert.That(buf[3]).IsEqualTo((byte)0x00).Because("high byte of OUI is masked out");
        await Assert.That(buf[4]).IsEqualTo((byte)0x11);
        await Assert.That(buf[5]).IsEqualTo((byte)0x22);
    }

    [Test]
    public async Task WriteHeader_SnapLlc_ExplicitEtherType_WrittenAtOffset6()
    {
        // An explicit EtherType must appear in bytes 6–7 after WriteHeader.
        LlcLayer layer = LlcLayer.CreateSnap(etherType: FB.Auto<ushort>.Explicit(0x0800));
        byte[] buf = new byte[8];

        layer.WriteHeader(buf.AsSpan());

        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(6, 2));
        await Assert.That(etherType).IsEqualTo((ushort)0x0800);
    }

    #endregion

    #region EtherType patching

    [Test]
    public async Task PatchNextProtocol_SnapAutoEtherType_PatchesOffset6InFrame()
    {
        // Auto EtherType in SNAP mode — PatchNextProtocol must write the inner-layer EtherType
        // at frame[myOffset + 6].
        LlcLayer layer = LlcLayer.CreateSnap(); // auto EtherType
        byte[] frame = new byte[32];

        layer.PatchNextProtocol(frame.AsSpan(), myOffset: 0, next: 0x0800);

        ushort patched = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(6, 2));
        await Assert.That(patched).IsEqualTo((ushort)0x0800);
    }

    [Test]
    public async Task PatchNextProtocol_SnapExplicitEtherType_DoesNotOverwrite()
    {
        // An explicit EtherType must not be patched over.
        LlcLayer layer = LlcLayer.CreateSnap(etherType: FB.Auto<ushort>.Explicit(0x0800));
        byte[] frame = new byte[32];

        layer.WriteHeader(frame.AsSpan());
        layer.PatchNextProtocol(frame.AsSpan(), myOffset: 0, next: 0x86DD);

        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(6, 2));
        await Assert.That(etherType).IsEqualTo((ushort)0x0800)
            .Because("explicit EtherType must never be patched over");
    }

    [Test]
    public async Task PatchNextProtocol_PlainLlc_NeverPatches()
    {
        // Plain LLC has no EtherType field; PatchNextProtocol must be a no-op.
        LlcLayer layer = new(dsap: 0x00, ssap: 0x00);
        byte[] frame = new byte[32]; // all zeros initially

        layer.PatchNextProtocol(frame.AsSpan(), myOffset: 0, next: 0x0800);

        // Frame should remain all zeros — no bytes written
        await Assert.That(frame.All(static b => b == 0)).IsTrue()
            .Because("plain LLC has no EtherType; PatchNextProtocol must not modify the frame");
    }

    #endregion
}
