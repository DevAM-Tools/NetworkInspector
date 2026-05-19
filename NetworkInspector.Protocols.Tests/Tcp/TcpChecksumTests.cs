// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for TCP checksum validation.
/// Verifies that the checksum field is correctly parsed and that validation
/// produces correct status when enabled via the tcp.verify_checksum setting.
/// </summary>
internal sealed class TcpChecksumTests
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

    private static byte[] BuildFrame(ReadOnlySpan<byte> payload = default)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(_ClientIp, _ServerIp);
        TcpLayer tcp = new(ClientPort, ServerPort, seqNum: 1000, ackNum: 0, flags: TcpFlags.Syn);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(payload);
    }

    /// <summary>Corrupts the TCP checksum by flipping the first byte.</summary>
    private static void CorruptChecksum(byte[] frame) =>
        // TCP checksum is at offset 14 (Eth) + 20 (IPv4) + 16 (TCP checksum offset) = 50
        frame[50] ^= 0xFF;

    #endregion

    #region Checksum Field

    [Test]
    public async Task Checksum_FieldPresent()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] frame = BuildFrame();
        Packet p = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        // tcp.checksum is always present
        await ProtocolTestHelper.AssertFieldExists(stack, p, "tcp.checksum").ConfigureAwait(false);
    }

    [Test]
    public async Task Checksum_Status_Good_WhenEnabled()
    {
        using Stack stack = ProtocolTestHelper.BuildStackWithSettings(
            ("tcp.verify_checksum", SettingValue.Bool(true)));

        byte[] frame = BuildFrame();
        Packet p = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertStringField(stack, p, "tcp.checksum.status", "[Good]").ConfigureAwait(false);
    }

    [Test]
    public async Task Checksum_Status_Bad_WhenCorrupted()
    {
        using Stack stack = ProtocolTestHelper.BuildStackWithSettings(
            ("tcp.verify_checksum", SettingValue.Bool(true)));

        byte[] frame = BuildFrame();
        CorruptChecksum(frame);
        Packet p = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertStringField(stack, p, "tcp.checksum.status", "[Bad]").ConfigureAwait(false);
    }

    [Test]
    public async Task Checksum_Status_Absent_WhenDisabled()
    {
        // Checksum verification is disabled by default
        using Stack stack = ProtocolTestHelper.BuildStack();

        byte[] frame = BuildFrame();
        Packet p = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        // When verification is disabled, status field should not be present
        await ProtocolTestHelper.AssertFieldNotPresent(stack, p, "tcp.checksum.status").ConfigureAwait(false);
    }

    [Test]
    public async Task Checksum_Good_WithPayload()
    {
        using Stack stack = ProtocolTestHelper.BuildStackWithSettings(
            ("tcp.verify_checksum", SettingValue.Bool(true)));

        byte[] payload = "Hello, TCP!"u8.ToArray();
        byte[] frame = BuildFrame(payload);
        Packet p = ProtocolTestHelper.ParseFrame(stack, frame, 0, Timestamp.FromMillis(0));

        await ProtocolTestHelper.AssertStringField(stack, p, "tcp.checksum.status", "[Good]").ConfigureAwait(false);
    }

    #endregion
}
