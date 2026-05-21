// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// TLS / DTLS edge-case tests with deliberately malformed inputs.
/// </summary>
internal sealed class TlsMalformedTests
{
    [Test]
    public async Task Parse_RecordTooShort_DoesNotThrow()
    {
        byte[] payload = [0x16, 0x03, 0x03]; // truncated record header
        byte[] frame = FrameBuilderTestExtensions.WrapRawTcp(payload, srcPort: 12345, dstPort: 443);
        (Stack stack, _) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        { /* no crash */
        }
    }

    [Test]
    public async Task Parse_RecordLengthLargerThanData_DoesNotThrow()
    {
        // Header claims 0xFFFF bytes but body is empty.
        byte[] payload = [0x17, 0x03, 0x03, 0xFF, 0xFF];
        byte[] frame = FrameBuilderTestExtensions.WrapRawTcp(payload, srcPort: 12345, dstPort: 443);
        (Stack stack, _) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        { /* no crash */
        }
    }

    [Test]
    public async Task Parse_DtlsRecordTooShort_DoesNotThrow()
    {
        byte[] payload = [0x16, 0xFE, 0xFD, 0x00]; // half a DTLS header
        byte[] frame = FrameBuilderTestExtensions.WrapRawUdp(payload, srcPort: 12345, dstPort: 443);
        (Stack stack, _) = ProtocolTestHelper.BuildAndParse(frame);
        using (stack)
        { /* no crash */
        }
    }
}
