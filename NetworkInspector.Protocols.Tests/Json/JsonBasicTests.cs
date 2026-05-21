// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for JSON protocol parsing (RFC 8259).
/// JSON is dispatched from HTTP via "application/json" content-type.
/// Exercises object, array, key-value, boolean, and null tokens.
/// </summary>
internal sealed class JsonBasicTests
{
    #region Frame helpers

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0x00, 0x11, 0x22, 0x33, 0x44, 0x55]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB]);

    private static readonly IPv4Address _ClientIp = new(0x0A000001); // 10.0.0.1
    private static readonly IPv4Address _ServerIp = new(0x0A000002); // 10.0.0.2

    /// <summary>
    /// Builds an Ethernet + IPv4 + TCP + HTTP POST frame carrying a JSON body.
    /// The HTTP Content-Type is "application/json" to trigger JSON dispatch.
    /// </summary>
    private static byte[] BuildJsonHttpFrame(string jsonBody)
    {
        string httpMessage =
            $"POST /api HTTP/1.1\r\n" +
            $"Host: example.com\r\n" +
            $"Content-Type: application/json\r\n" +
            $"Content-Length: {jsonBody.Length}\r\n" +
            "\r\n" +
            jsonBody;

        byte[] httpBytes = Encoding.ASCII.GetBytes(httpMessage);
        EthernetLayer eth = new(_DstMac, _SrcMac);
        IPv4Layer ip = new(_ClientIp, _ServerIp);
        TcpLayer tcp = new(49152, 80, seqNum: 1, ackNum: 0, flags: TcpFlags.Psh | TcpFlags.Ack);
        return FrameStack.Start(eth).Then(ip).Then(tcp).CreateWithFixedValues().EmitFrame(httpBytes);
    }

    #endregion

    #region Object parsing

    [Test]
    public async Task Parse_Json_SimpleObject_KeyPresent()
    {
        byte[] frame = BuildJsonHttpFrame("{\"name\":\"John\"}");
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // HTTP uses lazy parsing; MaterializeAll ensures HTTP body dispatch runs,
            // which calls JSON and appends the JSON lazy container for materialization.
            packet.MaterializeAll();
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "json.key").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Json_SimpleObject_KeyValue()
    {
        byte[] frame = BuildJsonHttpFrame("{\"name\":\"John\"}");
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            packet.MaterializeAll();
            await ProtocolTestHelper.AssertStringField(stack, packet, "json.key", "name").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Json_SimpleObject_StringValue()
    {
        byte[] frame = BuildJsonHttpFrame("{\"name\":\"John\"}");
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            packet.MaterializeAll();
            await ProtocolTestHelper.AssertStringField(stack, packet, "json.value.string", "John").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Json_Object_NumberValue()
    {
        byte[] frame = BuildJsonHttpFrame("{\"age\":30}");
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            packet.MaterializeAll();
            await ProtocolTestHelper.AssertStringField(stack, packet, "json.value.number", "30").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Json_Object_ContainerPresent()
    {
        byte[] frame = BuildJsonHttpFrame("{\"x\":1}");
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // Top-level json container must be present
            packet.MaterializeAll();
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "json").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Json_Object_ObjectFieldPresent()
    {
        byte[] frame = BuildJsonHttpFrame("{\"x\":1}");
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            packet.MaterializeAll();
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "json.object").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Json_Object_MemberFieldPresent()
    {
        byte[] frame = BuildJsonHttpFrame("{\"x\":1}");
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            packet.MaterializeAll();
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "json.member").ConfigureAwait(false);
        }
    }

    #endregion

    #region Boolean and null values

    [Test]
    public async Task Parse_Json_BooleanTrue_Present()
    {
        byte[] frame = BuildJsonHttpFrame("{\"active\":true}");
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            packet.MaterializeAll();
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "json.value.true").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Json_BooleanFalse_Present()
    {
        byte[] frame = BuildJsonHttpFrame("{\"deleted\":false}");
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            packet.MaterializeAll();
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "json.value.false").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Json_Null_Present()
    {
        byte[] frame = BuildJsonHttpFrame("{\"ptr\":null}");
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            packet.MaterializeAll();
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "json.value.null").ConfigureAwait(false);
        }
    }

    #endregion

    #region Array

    [Test]
    public async Task Parse_Json_Array_ArrayFieldPresent()
    {
        byte[] frame = BuildJsonHttpFrame("[1,2,3]");
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            packet.MaterializeAll();
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "json.array").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_Json_Array_NumberValue()
    {
        byte[] frame = BuildJsonHttpFrame("[42]");
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            packet.MaterializeAll();
            await ProtocolTestHelper.AssertStringField(stack, packet, "json.value.number", "42").ConfigureAwait(false);
        }
    }

    #endregion

    #region Malformed / edge cases

    [Test]
    public async Task Parse_Json_EmptyBody_JsonNotPresent()
    {
        // Empty JSON body (length = 0)
        // HTTP parses, but JSON is not dispatched for empty payload
        byte[] frame = BuildJsonHttpFrame(string.Empty);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        {
            // HTTP layer must still be present
            await ProtocolTestHelper.AssertFieldExists(stack, packet, "http.request").ConfigureAwait(false);
            // JSON container must not be populated for empty payload
            await ProtocolTestHelper.AssertFieldNotPresent(stack, packet, "json").ConfigureAwait(false);
        }
    }

    #endregion
}
