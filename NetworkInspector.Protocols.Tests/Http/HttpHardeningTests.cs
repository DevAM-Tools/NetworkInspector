// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using EagerIndex = NetworkInspector.Protocols.Tests.Index.EagerIndexGroupRegistrationTests;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for HTTP/1 parsing of hostile or malformed input: status codes, chunk sizes, Content-Length values and the
/// fixed header limits. Every frame goes through the full Ethernet + IPv4 + TCP + HTTP stack.
/// </summary>
internal sealed class HttpHardeningTests
{
    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);
    private static readonly IPv4Address _ClientIp = new(0x0A000001);
    private static readonly IPv4Address _ServerIp = new(0x0A000002);

    /// <summary>Builds a frame whose TCP payload is <paramref name="http"/>, sent to port 80.</summary>
    internal static byte[] BuildHttpFrame(byte[] http)
    {
        EthernetLayer eth = new(_DstMac, _SrcMac, maxFrameSize: 20000);
        IPv4Layer ip = new(_ClientIp, _ServerIp);
        TcpLayer tcp = new(49152, 80, seqNum: 1, ackNum: 0, flags: TcpFlags.Psh | TcpFlags.Ack);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(http);
    }

    private static byte[] _BuildHttpFrame(string http) => BuildHttpFrame(Encoding.ASCII.GetBytes(http));

    #region Status code

    [Test]
    [Arguments("1234567")]
    [Arguments("000200")]
    [Arguments("65536")]
    [Arguments("+20")]
    [Arguments("2x0")]
    public async Task StatusCodeThatIsNotAShortNumberIsNotShown(string code)
    {
        byte[] frame = _BuildHttpFrame($"HTTP/1.1 {code} Odd\r\n\r\n");

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "http.response").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "http.response.code").ConfigureAwait(false);
        }
    }

    [Test]
    [Arguments("200", 200UL)]
    [Arguments("20", 20UL)]
    [Arguments("1000", 1000UL)]
    [Arguments("65535", 65535UL)]
    public async Task StatusCodeShortNumberIsShown(string code, ulong expected)
    {
        byte[] frame = _BuildHttpFrame($"HTTP/1.1 {code} Fine\r\n\r\n");

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", expected).ConfigureAwait(false);
        }
    }

    #endregion

    #region Chunked bodies

    [Test]
    [Arguments("80000000")]
    [Arguments("FFFFFFFF")]
    [Arguments("ffffffff")]
    public async Task ChunkSizeAboveIntMaxIsRefused(string size)
    {
        // 0x80000000 and above do not fit an int. They must be refused (body shown undecoded), not wrapped to a negative
        // size that makes the decoder index with a negative position. Called directly: the stream detector already
        // refuses such a size, so the full stack never hands this body to the HTTP parser.
        byte[] body = Encoding.ASCII.GetBytes($"{size}\r\nAAAA\r\n0\r\n\r\n");

        ReadOnlyMemory<byte>? decoded = HttpProtocol.DechunkBody(body);

        await Assert.That(decoded.HasValue).IsFalse();
    }

    [Test]
    public async Task ChunkSizeOfIntMaxIsRefusedAsTooLarge()
    {
        byte[] body = Encoding.ASCII.GetBytes("7FFFFFFF\r\nAAAA\r\n0\r\n\r\n");

        ReadOnlyMemory<byte>? decoded = HttpProtocol.DechunkBody(body);

        await Assert.That(decoded.HasValue).IsFalse();
    }

    [Test]
    public async Task ChunkedBodyIsDecoded()
    {
        byte[] frame = _BuildHttpFrame("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nHello\r\n0\r\n\r\n");

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertBytesField(stack, packet, "http.payload.decoded", "Hello"u8.ToArray()).ConfigureAwait(false);
        }
    }

    #endregion

    #region Content-Length

    [Test]
    [Arguments("5 ")]
    [Arguments("5\t")]
    [Arguments(" 5")]
    public async Task ContentLengthWithSurroundingBlanksIsShownAndIndexedConsistently(string value)
    {
        byte[] frame = _BuildHttpFrame($"POST / HTTP/1.1\r\nContent-Length: {value}\r\n\r\nHello");

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.content_length", 5).ConfigureAwait(false);
        }
    }

    [Test]
    [Arguments("5x")]
    [Arguments("-5")]
    [Arguments("")]
    public async Task InvalidContentLengthIsNeitherShownNorIndexed(string value)
    {
        byte[] frame = _BuildHttpFrame($"POST / HTTP/1.1\r\nContent-Length: {value}\r\n\r\n");

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "http.content_length").ConfigureAwait(false);
        }
    }

    #endregion

    #region Header limits

    private static string _Headers(int count)
    {
        StringBuilder builder = new();
        for (int index = 0; index < count; index++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"X-{index}: v\r\n");
        }

        return builder.ToString();
    }

    [Test]
    public async Task MoreThan256HeadersReportsTheLimit()
    {
        byte[] frame = _BuildHttpFrame("GET / HTTP/1.1\r\n" + _Headers(300) + "\r\n");

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "http.error.header_limit").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "http.payload").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Exactly256HeadersEndCleanlyWithoutErrorAndWithoutEmptyLineInBody()
    {
        // 255 filler headers plus Content-Length make exactly 256 header lines, then the empty line and a 2-byte body.
        byte[] frame = _BuildHttpFrame("POST / HTTP/1.1\r\n" + _Headers(255) + "Content-Length: 2\r\n\r\nHi");

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "http.error.header_limit").ConfigureAwait(false);
            await ProtocolTestHelper.AssertBytesField(stack, packet, "http.payload", "Hi"u8.ToArray()).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task HeaderLineLongerThan8192BytesReportsTheLimit()
    {
        string longValue = new('a', 9000);
        byte[] frame = _BuildHttpFrame($"GET / HTTP/1.1\r\nHost: x\r\nX-Long: {longValue}\r\n\r\n");

        await EagerIndex.AssertAllEmittedFieldGroupsRecordedEagerly(frame).ConfigureAwait(false);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "http.error.header_limit").ConfigureAwait(false);
        }
    }

    #endregion
}
