// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for JSON parsing of crafted input: strings that end in a lone backslash and strings that reach the string length
/// limit (65536). JSON is reached through an HTTP body; large bodies arrive as a stream of TCP segments.
/// </summary>
internal sealed class JsonHardeningTests
{
    private const int _MaxStringLength = 65536;
    private const int _SegmentLength = 1400;
    private const ushort _ClientPort = 49152;
    private const ushort _ServerPort = 80;

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly IPv4Address _ClientIp = new(0x0A000001);
    private static readonly IPv4Address _ServerIp = new(0x0A000002);

    private static byte[] _ClientFrame(uint seq, uint ack, byte flags, ReadOnlySpan<byte> payload = default)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(_ClientIp, _ServerIp);
        TcpLayer tcp = new(_ClientPort, _ServerPort, seqNum: seq, ackNum: ack, flags: flags);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(payload);
    }

    private static byte[] _ServerFrame(uint seq, uint ack, byte flags)
    {
        EthernetLayer eth = new(_SrcMac, _DstMac);
        IPv4Layer ip = new(_ServerIp, _ClientIp);
        TcpLayer tcp = new(_ServerPort, _ClientPort, seqNum: seq, ackNum: ack, flags: flags);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    /// <summary>
    /// Sends an HTTP POST with the given JSON body over a fresh TCP connection, split into segments that fit one frame, and
    /// returns the packet of the last segment (the one that completes the message and carries the JSON layer).
    /// </summary>
    private static Packet _ParseJsonBody(Stack stack, byte[] body)
    {
        const uint clientIsn = 1000;
        const uint serverIsn = 2000;
        _ = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(clientIsn, 0, TcpFlags.Syn), 0, Timestamp.FromMillis(0));
        _ = ProtocolTestHelper.ParseFrame(stack, _ServerFrame(serverIsn, clientIsn + 1, TcpFlags.SynAck), 1, Timestamp.FromMillis(1));
        _ = ProtocolTestHelper.ParseFrame(stack, _ClientFrame(clientIsn + 1, serverIsn + 1, TcpFlags.Ack), 2, Timestamp.FromMillis(2));

        string head =
            "POST /api HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "\r\n";
        byte[] message = [.. Encoding.ASCII.GetBytes(head), .. body];

        Packet? last = null;
        int packetIndex = 3;
        for (int sent = 0; sent < message.Length; sent += _SegmentLength)
        {
            int length = Math.Min(_SegmentLength, message.Length - sent);
            last = ProtocolTestHelper.ParseFrame(
                stack,
                _ClientFrame(clientIsn + 1 + (uint)sent, serverIsn + 1, TcpFlags.Psh | TcpFlags.Ack, message.AsSpan(sent, length)),
                packetIndex,
                Timestamp.FromMillis(packetIndex));
            packetIndex++;
        }

        return last!; // the message is never empty, so the loop ran at least once
    }

    private static byte[] _StringDocument(string prefix, int fillCount, char fill)
    {
        return Encoding.ASCII.GetBytes("\"" + prefix + new string(fill, fillCount) + "\"");
    }

    #region Lone backslash

    [Test]
    [Arguments("\"\\")]
    [Arguments("{\"a\":\"x\\")]
    [Arguments("{\"k\\")]
    [Arguments("[\"abc\\")]
    public async Task StringEndingInALoneBackslashDoesNotThrow(string json)
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet packet = _ParseJsonBody(stack, Encoding.ASCII.GetBytes(json));

        packet.MaterializeAll();

        await ProtocolTestHelper.AssertFieldExists(stack, packet, "json").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
    }

    [Test]
    public async Task StringEndingInALoneBackslashKeepsTheCharactersBeforeIt()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet packet = _ParseJsonBody(stack, Encoding.ASCII.GetBytes("{\"a\":\"xy\\"));

        packet.MaterializeAll();

        await ProtocolTestHelper.AssertStringField(stack, packet, "json.value.string", "xy\\").ConfigureAwait(false);
    }

    #endregion

    #region String length limit

    [Test]
    public async Task EscapedStringLongerThanTheLimitIsTruncatedWithAnIndicator()
    {
        // A leading escape selects the slow decoder; the long run behind it is longer than the room left in its buffer.
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet packet = _ParseJsonBody(stack, _StringDocument("\\n", _MaxStringLength + 4000, 'a'));

        packet.MaterializeAll();

        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "packet.error").ConfigureAwait(false);
        await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "json.error.string_limit", "_MaxStringLength").ConfigureAwait(false);
        FieldId? fieldId = stack.GetFieldId("json.value.string");
        await Assert.That(fieldId).IsNotNull();
        await Assert.That(packet.TryGetFieldValue(fieldId!.Value, out FieldValue value, materialize: true)).IsTrue();
        await Assert.That(value.Data.TryGetAsString(out string? text)).IsTrue();
        await Assert.That(text!.Length).IsEqualTo(_MaxStringLength);
    }

    [Test]
    public async Task UnescapedStringLongerThanTheLimitIsTruncatedWithAnIndicator()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet packet = _ParseJsonBody(stack, _StringDocument(string.Empty, _MaxStringLength + 1, 'a'));

        packet.MaterializeAll();

        await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "json.error.string_limit", "_MaxStringLength").ConfigureAwait(false);
    }

    [Test]
    public async Task UnescapedStringOfExactlyTheLimitHasNoIndicator()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet packet = _ParseJsonBody(stack, _StringDocument(string.Empty, _MaxStringLength, 'a'));

        packet.MaterializeAll();

        await ProtocolTestHelper.AssertFieldExists(stack, packet, "json.value.string").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "json.error.string_limit").ConfigureAwait(false);
    }

    [Test]
    public async Task EscapedStringOfExactlyTheLimitHasNoIndicator()
    {
        // One escape (decodes to one char) plus filler: the decoded string is exactly the limit and nothing is lost.
        using Stack stack = ProtocolTestHelper.BuildStack();
        Packet packet = _ParseJsonBody(stack, _StringDocument("\\n", _MaxStringLength - 1, 'a'));

        packet.MaterializeAll();

        await ProtocolTestHelper.AssertFieldExists(stack, packet, "json.value.string").ConfigureAwait(false);
        await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "json.error.string_limit").ConfigureAwait(false);
    }

    [Test]
    public async Task KeyLongerThanTheLimitIsTruncatedWithAnIndicator()
    {
        using Stack stack = ProtocolTestHelper.BuildStack();
        byte[] body = Encoding.ASCII.GetBytes("{\"" + new string('k', _MaxStringLength + 1) + "\":1}");
        Packet packet = _ParseJsonBody(stack, body);

        packet.MaterializeAll();

        await ProtocolTestHelper.AssertStringFieldContains(stack, packet, "json.error.string_limit", "_MaxStringLength").ConfigureAwait(false);
    }

    #endregion
}
