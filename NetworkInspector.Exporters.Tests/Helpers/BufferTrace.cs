// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Helpers;

/// <summary>
/// Builds a small capture whose packets carry the additional buffers that protocols attach to a packet: the datagram that an IPv4
/// fragment completes, the PDU that two TCP segments complete, the decoded body of a chunked HTTP response, and the unmasked payload
/// of a WebSocket frame. Three packets of the capture (the first fragment, the first request segment, and a plain datagram) have no
/// additional buffer at all. The buffer export tests feed the capture to the exporters and compare what they wrote with what the packets
/// themselves report.
/// </summary>
/// <remarks>
/// <para>
/// Every call builds a capture that is unique on the thread's stack: the IPv4 identification and the TCP client ports change per
/// call (each call owns two ports: the HTTP connection and the upgrade connection one port above it, so no two calls share a
/// connection), because the shared test stack keeps its reassembly state across tests and a repeated connection would be read as a
/// retransmission.
/// </para>
/// <para>Thread safety: the run counter is incremented atomically; the frames are independent values.</para>
/// </remarks>
internal static class BufferTrace
{
    #region Payloads and capture counter

    /// <summary>Number of captures built so far; makes the connection identity of each capture unique.</summary>
    private static int _Run;

    /// <summary>The plain UDP payload that the two fragments of the first datagram carry together.</summary>
    internal static ReadOnlyMemory<byte> FragmentedPayload { get; } = Enumerable.Range(1, 24).Select(static i => checked((byte)i)).ToArray();

    /// <summary>The JSON text that the chunked response carries as its decoded body.</summary>
    internal static ReadOnlyMemory<byte> DecodedBody { get; } = "{\"decoded\":\"chunked body\"}"u8.ToArray();

    /// <summary>The text that the masked WebSocket frame carries after unmasking.</summary>
    internal static ReadOnlyMemory<byte> UnmaskedPayload { get; } = "unmasked websocket text"u8.ToArray();

    #endregion

    #region Building the capture

    /// <summary>
    /// Builds the capture: seven frames, in the order the stack must parse them (the first frame of a datagram or a PDU is held until
    /// the last one arrives).
    /// </summary>
    /// <returns>
    /// The frames. Frame 1 completes the fragmented datagram, frame 3 completes the HTTP request, frame 4 is a chunked response, frame 5
    /// is a WebSocket upgrade with a masked frame, and frames 0, 2, and 6 carry no additional buffer.
    /// </returns>
    internal static List<Frame> Create()
    {
        int run = Interlocked.Increment(ref _Run);
        ushort identification = checked((ushort)(0x4000 + (run % 0x3000)));
        ushort clientPort = checked((ushort)(20_000 + (2 * (run % 10_000)))); // the upgrade connection takes clientPort + 1, so each run owns two ports

        // The datagram: a UDP header and the payload, cut into two fragments at a multiple of eight bytes (the offset unit).
        byte[] datagram = FrameGenerators.BuildUdpDatagram(40000, 40001, FragmentedPayload.Span);
        byte[] request = "GET /index HTTP/1.1\r\nHost: example.test\r\n\r\n"u8.ToArray();
        int split = 30;

        // A chunked response: the chunk size line in hex, the chunk, and the terminal chunk (RFC 9112 section 7.1).
        byte[] chunkSize = Encoding.ASCII.GetBytes(DecodedBody.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n");
        byte[] chunked = [.. chunkSize, .. DecodedBody.Span, .. "\r\n0\r\n\r\n"u8];
        byte[] response = [.. "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Type: application/json\r\n\r\n"u8, .. chunked];

        // The upgrade response and the first WebSocket frame travel in one segment, like the protocol tests of the dissector do.
        byte[] upgrade =
        [
            .. "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n"u8,
            .. FrameGenerators.BuildMaskedWebSocketTextFrame(UnmaskedPayload.Span, 0xDEADBEEF),
        ];

        // The frames are built one after the other so that every line stays short and names what the frame is.
        byte[] fragmentFirst = FrameGenerators.BuildEthernetIpv4FragmentFrame(identification, 0, true, 17, datagram.AsSpan(0, 16));
        byte[] fragmentLast = FrameGenerators.BuildEthernetIpv4FragmentFrame(identification, 16, false, 17, datagram.AsSpan(16));
        byte[] requestFirst = FrameGenerators.BuildEthernetIpv4TcpDataFrame(clientPort, 1000, request.AsSpan(0, split), false);
        byte[] requestLast = FrameGenerators.BuildEthernetIpv4TcpDataFrame(clientPort, 1000 + checked((uint)split),request.AsSpan(split), false);
        byte[] chunkedResponse = FrameGenerators.BuildEthernetIpv4TcpDataFrame(clientPort, 5000, response, true);
        byte[] upgradeFrame = FrameGenerators.BuildEthernetIpv4TcpDataFrame(checked((ushort)(clientPort + 1)), 9000, upgrade, true);
        byte[] plainUdp = FrameGenerators.BuildEthernetIpv4UdpFrame(18);
        return
        [
            TestHarness.CreateFrame(0, 1_000_000, fragmentFirst),
            TestHarness.CreateFrame(1, 2_000_000, fragmentLast),
            TestHarness.CreateFrame(2, 3_000_000, requestFirst),
            TestHarness.CreateFrame(3, 4_000_000, requestLast),
            TestHarness.CreateFrame(4, 5_000_000, chunkedResponse),
            TestHarness.CreateFrame(5, 6_000_000, upgradeFrame),
            TestHarness.CreateFrame(6, 7_000_000, plainUdp),
        ];
    }

    #endregion
}
