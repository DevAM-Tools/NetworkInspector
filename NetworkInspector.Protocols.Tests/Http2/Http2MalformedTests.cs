// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// HTTP/2 malformed-frame tests.
/// </summary>
internal sealed class Http2MalformedTests
{
    [Test]
    public async Task Parse_TruncatedFrameHeader_DoesNotThrow()
    {
        byte[] payload = [0x00, 0x00, 0x08]; // only length, no type/flags/stream
        byte[] frame = FrameBuilderTestExtensions.WrapRawTcp(payload, srcPort: 12345, dstPort: 8443);
        (Stack stack, _) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        { /* no crash */
        }
    }

    [Test]
    public async Task Parse_FrameLengthExceedsBuffer_DoesNotThrow()
    {
        // header claims 0xFFFF payload but buffer is too short
        byte[] payload = [0x00, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xAB];
        byte[] frame = FrameBuilderTestExtensions.WrapRawTcp(payload, srcPort: 12345, dstPort: 8443);
        (Stack stack, _) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        { /* no crash */
        }
    }
}
