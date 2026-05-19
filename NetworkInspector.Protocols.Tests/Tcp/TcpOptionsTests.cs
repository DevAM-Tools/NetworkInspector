// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for TCP options parsing: MSS, Window Scale, SACK Permitted, SACK blocks,
/// Timestamps, NOP, EOL, and unknown options.
/// Uses <see cref="TcpLayerWithOptions"/> from the new cons-list FrameBuilder API.
/// </summary>
internal sealed class TcpOptionsTests
{
    #region Constants

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly IPv4Address _ClientIp = new(0x0A000001);
    private static readonly IPv4Address _ServerIp = new(0x0A000002);

    private const ushort ClientPort = 49152;
    private const ushort ServerPort = 80;

    #endregion

    #region Helpers

    /// <summary>
    /// Builds an Ethernet + IPv4 + TCP frame whose TCP header carries the given
    /// pre-encoded option bytes.  Padding to a 4-byte boundary is performed by
    /// <see cref="TcpLayerWithOptions"/>.
    /// </summary>
    private static byte[] BuildFrameWithOptions(byte flags, byte[] optionBytes)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(_ClientIp, _ServerIp);
        TcpLayerWithOptions tcp = new(
            ClientPort, ServerPort, optionBytes,
            seqNum: 1000, ackNum: 0, flags: flags);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Builds the standard SYN options block:
    /// MSS(4) + SACKPerm(2) + NOP+NOP+Timestamps(12) + NOP(1) + WScale(3) = 22 bytes.
    /// (The 4-byte boundary padding is added by <see cref="TcpLayerWithOptions"/>.)
    /// </summary>
    private static byte[] BuildSynOptionsBytes(ushort mss, byte windowScale, uint tsVal, uint tsEcr)
    {
        byte[] opts = new byte[22];
        Span<byte> s = opts;

        // MSS
        s[0] = 0x02;
        s[1] = 0x04;
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], mss);

        // SACK Permitted
        s[4] = 0x04;
        s[5] = 0x02;

        // NOP + NOP + Timestamps
        s[6] = 0x01;
        s[7] = 0x01;
        s[8] = 0x08;
        s[9] = 0x0A;
        BinaryPrimitives.WriteUInt32BigEndian(s[10..], tsVal);
        BinaryPrimitives.WriteUInt32BigEndian(s[14..], tsEcr);

        // NOP + Window Scale
        s[18] = 0x01;
        s[19] = 0x03;
        s[20] = 0x03;
        s[21] = windowScale;

        return opts;
    }

    /// <summary>
    /// Builds an Ethernet + IPv4 + TCP SYN frame with standard SYN options
    /// (MSS + SACK Permitted + Timestamps + NOP + Window Scale).
    /// </summary>
    private static byte[] BuildSynWithOptions(
        ushort mss = 1460,
        byte windowScale = 7,
        uint tsVal = 0,
        uint tsEcr = 0) =>
        BuildFrameWithOptions(TcpFlags.Syn, BuildSynOptionsBytes(mss, windowScale, tsVal, tsEcr));

    private static Packet Parse(Stack stack, byte[] frameData) =>
        ProtocolTestHelper.ParseFrame(stack, frameData, 0, Timestamp.FromMillis(0));

    #endregion

    #region MSS Option

    [Test]
    public async Task Options_Mss_Parsed()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] frame = BuildSynWithOptions(mss: 1460);
        Packet p = Parse(stack, frame);

        await ProtocolTestHelper.AssertU64Field(stack, p, "tcp.options.mss_val", 1460).ConfigureAwait(false);
    }

    [Test]
    public async Task Options_Mss_CustomValue()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] frame = BuildSynWithOptions(mss: 536);
        Packet p = Parse(stack, frame);

        await ProtocolTestHelper.AssertU64Field(stack, p, "tcp.options.mss_val", 536).ConfigureAwait(false);
    }

    #endregion

    #region Window Scale Option

    [Test]
    public async Task Options_WindowScale_ShiftCount()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] frame = BuildSynWithOptions(windowScale: 7);
        Packet p = Parse(stack, frame);

        await ProtocolTestHelper.AssertU64Field(stack, p, "tcp.options.wscale.shift", 7).ConfigureAwait(false);
    }

    [Test]
    public async Task Options_WindowScale_Multiplier()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        // Shift count 7 → multiplier = 2^7 = 128
        byte[] frame = BuildSynWithOptions(windowScale: 7);
        Packet p = Parse(stack, frame);

        await ProtocolTestHelper.AssertU64Field(stack, p, "tcp.options.wscale.multiplier", 128).ConfigureAwait(false);
    }

    #endregion

    #region SACK Permitted

    [Test]
    public async Task Options_SackPermitted_Present()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] frame = BuildSynWithOptions();
        Packet p = Parse(stack, frame);

        // SACK Permitted option should be present in SYN options
        await ProtocolTestHelper.AssertFieldExists(stack, p, "tcp.options.sack_perm").ConfigureAwait(false);
    }

    #endregion

    #region Timestamps

    [Test]
    public async Task Options_Timestamps_TsVal()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] frame = BuildSynWithOptions(tsVal: 123456);
        Packet p = Parse(stack, frame);

        await ProtocolTestHelper.AssertU64Field(stack, p, "tcp.options.timestamp.tsval", 123456).ConfigureAwait(false);
    }

    [Test]
    public async Task Options_Timestamps_TsEcr()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] frame = BuildSynWithOptions(tsVal: 100, tsEcr: 789012);
        Packet p = Parse(stack, frame);

        await ProtocolTestHelper.AssertU64Field(stack, p, "tcp.options.timestamp.tsecr", 789012).ConfigureAwait(false);
    }

    #endregion

    #region SACK Blocks

    [Test]
    public async Task Options_Sack_SingleBlock()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        // SACK option with 1 block: Kind(1) + Length(1) + Block(8) = 10 bytes
        // preceded by 2 NOPs for alignment → 12 total
        byte[] sackOpts = new byte[12];
        sackOpts[0] = 0x01; // NOP
        sackOpts[1] = 0x01; // NOP
        sackOpts[2] = 0x05; // SACK kind
        sackOpts[3] = 0x0A; // Length = 10
        BinaryPrimitives.WriteUInt32BigEndian(sackOpts.AsSpan(4), 1001); // Left edge
        BinaryPrimitives.WriteUInt32BigEndian(sackOpts.AsSpan(8), 1101); // Right edge
        byte[] frame = BuildFrameWithOptions(TcpFlags.Ack, sackOpts);
        Packet p = Parse(stack, frame);

        await ProtocolTestHelper.AssertU64Field(stack, p, "tcp.options.sack.count", 1).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, p, "tcp.options.sack_le", 1001).ConfigureAwait(false);
        await ProtocolTestHelper.AssertU64Field(stack, p, "tcp.options.sack_re", 1101).ConfigureAwait(false);
    }

    #endregion

    #region EOL and NOP

    [Test]
    public async Task Options_Nop_Present()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        // SYN options include NOP padding
        byte[] frame = BuildSynWithOptions();
        Packet p = Parse(stack, frame);

        await ProtocolTestHelper.AssertFieldExists(stack, p, "tcp.options.nop").ConfigureAwait(false);
    }

    [Test]
    public async Task Options_Eol_Present()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        // Write MSS (4 bytes) + EOL (1 byte) → 5 bytes, padded to 8
        byte[] eolOpts = new byte[5];
        eolOpts[0] = 0x02; // MSS kind
        eolOpts[1] = 0x04; // Length
        BinaryPrimitives.WriteUInt16BigEndian(eolOpts.AsSpan(2), 1460);
        eolOpts[4] = 0x00; // EOL
        byte[] frame = BuildFrameWithOptions(TcpFlags.Syn, eolOpts);
        Packet p = Parse(stack, frame);

        await ProtocolTestHelper.AssertFieldExists(stack, p, "tcp.options.eol").ConfigureAwait(false);
    }

    #endregion

    #region Options Container

    [Test]
    public async Task Options_Container_Present_WhenOptionsExist()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] frame = BuildSynWithOptions();
        Packet p = Parse(stack, frame);

        await ProtocolTestHelper.AssertFieldExists(stack, p, "tcp.options").ConfigureAwait(false);
    }

    [Test]
    public async Task Options_Container_Absent_WhenNoOptions()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        // Plain TCP frame without options (DataOffset = 5)
        EthernetLayer ethLayer = new(_DstMac, _SrcMac);
        IPv4Layer ipLayer = new(_ClientIp, _ServerIp);
        TcpLayer tcpLayer = new(ClientPort, ServerPort, seqNum: 1000, ackNum: 0, flags: TcpFlags.Syn);
        byte[] buffer = FrameStack.Start(ethLayer).Then(ipLayer).Then(tcpLayer).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
        Packet p = Parse(stack, buffer);

        await ProtocolTestHelper.AssertFieldNotPresent(stack, p, "tcp.options").ConfigureAwait(false);
    }

    #endregion

    #region Display Text

    [Test]
    public async Task Options_Mss_DisplayText()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] frame = BuildSynWithOptions(mss: 1460);
        Packet p = Parse(stack, frame);

        await ProtocolTestHelper.AssertDisplayText(stack, p, "tcp.options.mss", "Maximum Segment Size: 1460 bytes").ConfigureAwait(false);
    }

    #endregion
}
