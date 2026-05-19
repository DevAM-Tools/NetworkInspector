// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using System.Text;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for HTTP/1.x protocol parsing (RFC 7230–7235).
/// Verifies request line, response line, header fields, and content-type dispatch.
/// HTTP is carried over TCP port 80; the full Ethernet + IPv4 + TCP stack is exercised.
/// </summary>
internal sealed class HttpBasicTests
{
    #region Frame helpers

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);

    // 10.0.0.1 and 10.0.0.2
    private static readonly IPv4Address _ClientIp = new(0x0A000001);
    private static readonly IPv4Address _ServerIp = new(0x0A000002);

    /// <summary>
    /// Builds an Ethernet + IPv4 + TCP + HTTP payload frame.
    /// The TCP destination port is 80 so the stack dispatches to HTTP.
    /// </summary>
    private static byte[] BuildHttpFrame(string httpMessage, ushort dstPort = 80)
    {
        byte[] httpBytes = Encoding.ASCII.GetBytes(httpMessage);
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(_ClientIp, _ServerIp);
        TcpLayer tcp = new(49152, dstPort, seqNum: 1, ackNum: 0, flags: TcpFlags.Psh | TcpFlags.Ack);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(httpBytes);
    }

    private const string HttpGetRequest =
        "GET /index.html HTTP/1.1\r\n" +
        "Host: example.com\r\n" +
        "User-Agent: TestAgent/1.0\r\n" +
        "\r\n";

    private const string HttpPostRequest =
        "POST /api/data HTTP/1.1\r\n" +
        "Host: api.example.com\r\n" +
        "Content-Type: application/json\r\n" +
        "Content-Length: 2\r\n" +
        "\r\n" +
        "{}";

    private const string Http200Response =
        "HTTP/1.1 200 OK\r\n" +
        "Content-Type: text/plain\r\n" +
        "Content-Length: 5\r\n" +
        "\r\n" +
        "Hello";

    private const string Http404Response =
        "HTTP/1.1 404 Not Found\r\n" +
        "Content-Length: 0\r\n" +
        "\r\n";

    private const string Http101Response =
        "HTTP/1.1 101 Switching Protocols\r\n" +
        "Upgrade: websocket\r\n" +
        "Connection: Upgrade\r\n" +
        "\r\n";

    private const string HttpChunkedResponse =
        "HTTP/1.1 200 OK\r\n" +
        "Transfer-Encoding: chunked\r\n" +
        "\r\n" +
        "5\r\n" +
        "Hello\r\n" +
        "0\r\n" +
        "\r\n";

    #endregion

    #region Request parsing

    [Test]
    public async Task Parse_Http_Request_IsPresent()
    {
        byte[] frame = BuildHttpFrame(HttpGetRequest);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "http.request").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Http_Request_Method_GET()
    {
        byte[] frame = BuildHttpFrame(HttpGetRequest);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "http.request.method", "GET").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Http_Request_Uri()
    {
        byte[] frame = BuildHttpFrame(HttpGetRequest);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "http.request.uri", "/index.html").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Http_Request_Version()
    {
        byte[] frame = BuildHttpFrame(HttpGetRequest);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "http.request.version", "HTTP/1.1").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Http_Request_Host_Header()
    {
        byte[] frame = BuildHttpFrame(HttpGetRequest);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "http.host", "example.com").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Http_Request_UserAgent_Header()
    {
        byte[] frame = BuildHttpFrame(HttpGetRequest);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "http.user_agent", "TestAgent/1.0").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Http_Post_Method()
    {
        byte[] frame = BuildHttpFrame(HttpPostRequest);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "http.request.method", "POST").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Http_Post_ContentType_Header()
    {
        byte[] frame = BuildHttpFrame(HttpPostRequest);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "http.content_type_value", "application/json").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Http_Post_ContentLength_Header()
    {
        byte[] frame = BuildHttpFrame(HttpPostRequest);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.content_length", 2).ConfigureAwait(false);
        }
    }

    #endregion

    #region Response parsing

    [Test]
    public async Task Parse_Http_Response_IsPresent()
    {
        byte[] frame = BuildHttpFrame(Http200Response);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "http.response").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Http_Response_StatusCode_200()
    {
        byte[] frame = BuildHttpFrame(Http200Response);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 200).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Http_Response_ReasonPhrase_OK()
    {
        byte[] frame = BuildHttpFrame(Http200Response);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "http.response.phrase", "OK").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Http_Response_StatusCode_404()
    {
        byte[] frame = BuildHttpFrame(Http404Response);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.response.code", 404).ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Http_Response_ReasonPhrase_NotFound()
    {
        byte[] frame = BuildHttpFrame(Http404Response);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "http.response.phrase", "Not Found").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Http_Response_Version()
    {
        byte[] frame = BuildHttpFrame(Http200Response);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "http.response.version", "HTTP/1.1").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Http_Response_ContentLength()
    {
        byte[] frame = BuildHttpFrame(Http200Response);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertU64Field(stack, packet, "http.content_length", 5).ConfigureAwait(false);
        }
    }

    #endregion

    #region Upgrade dispatch

    [Test]
    public async Task Parse_Http_101_UpgradeFieldPresent()
    {
        byte[] frame = BuildHttpFrame(Http101Response);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "http.upgrade").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Http_101_UpgradeValue_Websocket()
    {
        byte[] frame = BuildHttpFrame(Http101Response);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "http.upgrade", "websocket").ConfigureAwait(false);
        }
    }

    #endregion

    #region Transfer-Encoding: chunked

    [Test]
    public async Task Parse_Http_Response_ChunkedEncoding_FieldPresent()
    {
        byte[] frame = BuildHttpFrame(HttpChunkedResponse);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            await ProtocolTestHelper.AssertStringField(stack, packet, "http.transfer_encoding", "chunked").ConfigureAwait(false);
        }
    }

    #endregion

    #region Alternate port 8080

    [Test]
    public async Task Parse_Http_OnPort8080_RequestPresent()
    {
        byte[] frame = BuildHttpFrame(HttpGetRequest, dstPort: 8080);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // HTTP protocol must be detected on port 8080 as well
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "http.request.method").ConfigureAwait(false);
        }
    }

    #endregion

    #region Malformed — non-HTTP payload on port 80

    [Test]
    public async Task Parse_Http_NonHttpPayload_NotParsedAsHttp()
    {
        // Random binary data on TCP port 80 — HTTP parser returns 0 (no match)
        byte[] httpBytes = [0xFF, 0xFE, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05];
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(_ClientIp, _ServerIp);
        TcpLayer tcp = new(49152, 80, seqNum: 1, ackNum: 0, flags: TcpFlags.Psh | TcpFlags.Ack);

        byte[] frame = FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(httpBytes);

        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // HTTP fields must NOT be present when the payload is not an HTTP message
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "http.request").ConfigureAwait(false);
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "http.response").ConfigureAwait(false);
        }
    }

    #endregion
}
