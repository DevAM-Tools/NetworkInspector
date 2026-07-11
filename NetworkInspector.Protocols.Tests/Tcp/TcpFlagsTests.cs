// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for TCP flags parsing, sub-fields, and display text formatting.
/// Verifies individual flag bits, combinations, and the display text format.
/// </summary>
internal sealed class TcpFlagsTests
{
    #region Constants

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly IPv4Address _ClientIp = new(0x0A000001);
    private static readonly IPv4Address _ServerIp = new(0x0A000002);

    private const ushort _ClientPort = 49152;
    private const ushort _ServerPort = 80;

    #endregion

    #region Helpers

    private static byte[] _BuildFrame(byte flags)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(_ClientIp, _ServerIp);
        TcpLayer tcp = new(_ClientPort, _ServerPort, seqNum: 1000, ackNum: 0, flags: flags);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    private static Packet _Parse(Stack stack, byte[] frameData) =>
        ProtocolTestHelper.ParseFrame(stack, frameData, 0, Timestamp.FromMillis(0));

    #endregion

    #region Individual Flags

    [Test]
    public async Task Flags_Syn_Only()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet p = _Parse(stack, _BuildFrame(TcpFlags.Syn));

        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.syn", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.ack", false).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.fin", false).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.reset", false).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.push", false).ConfigureAwait(false);
    }

    [Test]
    public async Task Flags_Ack_Only()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet p = _Parse(stack, _BuildFrame(TcpFlags.Ack));

        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.ack", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.syn", false).ConfigureAwait(false);
    }

    [Test]
    public async Task Flags_Fin_Only()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet p = _Parse(stack, _BuildFrame(TcpFlags.Fin));

        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.fin", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.syn", false).ConfigureAwait(false);
    }

    [Test]
    public async Task Flags_Rst_Only()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet p = _Parse(stack, _BuildFrame(TcpFlags.Rst));

        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.reset", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.syn", false).ConfigureAwait(false);
    }

    [Test]
    public async Task Flags_Psh_Only()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet p = _Parse(stack, _BuildFrame(TcpFlags.Psh));

        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.push", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.syn", false).ConfigureAwait(false);
    }

    [Test]
    public async Task Flags_Urg_Only()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet p = _Parse(stack, _BuildFrame(TcpFlags.Urg));

        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.urg", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.syn", false).ConfigureAwait(false);
    }

    [Test]
    public async Task Flags_Ece_Only()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet p = _Parse(stack, _BuildFrame(TcpFlags.Ece));

        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.ece", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.syn", false).ConfigureAwait(false);
    }

    [Test]
    public async Task Flags_Cwr_Only()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet p = _Parse(stack, _BuildFrame(TcpFlags.Cwr));

        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.cwr", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.syn", false).ConfigureAwait(false);
    }

    #endregion

    #region Combinations

    [Test]
    public async Task Flags_SynAck()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet p = _Parse(stack, _BuildFrame(TcpFlags.SynAck));

        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.syn", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.ack", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.fin", false).ConfigureAwait(false);
    }

    [Test]
    public async Task Flags_FinAck()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet p = _Parse(stack, _BuildFrame(TcpFlags.FinAck));

        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.fin", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.ack", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.syn", false).ConfigureAwait(false);
    }

    [Test]
    public async Task Flags_PshAck()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet p = _Parse(stack, _BuildFrame(TcpFlags.PshAck));

        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.push", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.ack", true).ConfigureAwait(false);
    }

    [Test]
    public async Task Flags_ChristmasTree_AllSet()
    {
        // Christmas tree packet: FIN+SYN+RST+PSH+ACK+URG
        byte xmasFlags = (byte)(TcpFlags.Fin | TcpFlags.Syn | TcpFlags.Rst |
                                TcpFlags.Psh | TcpFlags.Ack | TcpFlags.Urg);
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet p = _Parse(stack, _BuildFrame(xmasFlags));

        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.fin", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.syn", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.reset", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.push", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.ack", true).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.urg", true).ConfigureAwait(false);
    }

    [Test]
    public async Task Flags_NullScan_NoneSet()
    {
        // Null scan: no flags set
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet p = _Parse(stack, _BuildFrame(0x00));

        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.fin", false).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.syn", false).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.reset", false).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.push", false).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.ack", false).ConfigureAwait(false);
        await ProtocolTestHelper.AssertBoolField(stack, p, "tcp.flags.urg", false).ConfigureAwait(false);
    }

    #endregion

    #region Flags Container Value

    [Test]
    public async Task Flags_ContainerValue_IsNumericBitmask()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet p = _Parse(stack, _BuildFrame(TcpFlags.SynAck));

        // tcp.flags stores the raw byte as U64
        await ProtocolTestHelper.AssertU64Field(stack, p, "tcp.flags", TcpFlags.SynAck).ConfigureAwait(false);
    }

    [Test]
    public async Task Flags_ContainerValue_AllFlags()
    {
        byte all = 0xFF;
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet p = _Parse(stack, _BuildFrame(all));

        await ProtocolTestHelper.AssertU64Field(stack, p, "tcp.flags", all).ConfigureAwait(false);
    }

    #endregion

    #region Display Text

    [Test]
    public async Task Flags_DisplayText_SynOnly()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet p = _Parse(stack, _BuildFrame(TcpFlags.Syn));

        // Display text format: "0xHH [flag1, flag2, ...]"
        await ProtocolTestHelper.AssertDisplayText(stack, p, "tcp.flags", "0x02 [SYN]").ConfigureAwait(false);
    }

    [Test]
    public async Task Flags_DisplayText_SynAck()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet p = _Parse(stack, _BuildFrame(TcpFlags.SynAck));

        await ProtocolTestHelper.AssertDisplayText(stack, p, "tcp.flags", "0x12 [ACK, SYN]").ConfigureAwait(false);
    }

    [Test]
    public async Task Flags_DisplayText_None()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet p = _Parse(stack, _BuildFrame(0x00));

        await ProtocolTestHelper.AssertDisplayText(stack, p, "tcp.flags", "0x00 [None]").ConfigureAwait(false);
    }

    #endregion
}
