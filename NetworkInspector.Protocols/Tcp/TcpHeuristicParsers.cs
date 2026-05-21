// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// Heuristic parser that detects HTTP/1.x by inspecting the first bytes of TCP payload.
/// Matches well-known HTTP method keywords (GET, POST, PUT, DELETE, HEAD, OPTIONS, PATCH, CONNECT, TRACE)
/// and response prefix "HTTP/".
/// </summary>
internal sealed class HttpHeuristicParser(ProtocolId protocolId) : IHeuristicParser
{
    public ProtocolId ProtocolId { get; } = protocolId;
    public string Name => "http.heuristic";
    public string UiName => "HTTP Heuristic";
    public string? Description => "Detects HTTP/1.x by method or response prefix";

    public bool Test(ReadOnlyMemory<byte> data)
    {
        // Minimum meaningful HTTP request: "GET / HTTP/1.0\r\n" = 16 bytes
        // Minimum HTTP response: "HTTP/1.0 200\r\n" = 15 bytes
        if (data.Length < 4)
        {
            return false;
        }

        ReadOnlySpan<byte> span = data.Span;

        // Check for HTTP response: "HTTP/"
        if (span.Length >= 5
            && span[0] == (byte)'H'
            && span[1] == (byte)'T'
            && span[2] == (byte)'T'
            && span[3] == (byte)'P'
            && span[4] == (byte)'/')
        {
            return true;
        }

        // Check for HTTP methods followed by a space
        return MatchesMethod(span, "GET "u8)
            || MatchesMethod(span, "POST "u8)
            || MatchesMethod(span, "PUT "u8)
            || MatchesMethod(span, "HEAD "u8)
            || MatchesMethod(span, "DELETE "u8)
            || MatchesMethod(span, "OPTIONS "u8)
            || MatchesMethod(span, "PATCH "u8)
            || MatchesMethod(span, "CONNECT "u8)
            || MatchesMethod(span, "TRACE "u8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MatchesMethod(ReadOnlySpan<byte> data, ReadOnlySpan<byte> method) =>
        data.Length >= method.Length && data[..method.Length].SequenceEqual(method);
}

/// <summary>
/// Heuristic parser that detects TLS/SSL by inspecting the TLS record header.
/// Matches ContentType 20–23 (ChangeCipherSpec, Alert, Handshake, ApplicationData)
/// with a valid TLS version (0x0300–0x0304).
/// </summary>
internal sealed class TlsHeuristicParser(ProtocolId protocolId) : IHeuristicParser
{
    public ProtocolId ProtocolId { get; } = protocolId;
    public string Name => "tls.heuristic";
    public string UiName => "TLS/SSL Heuristic";
    public string? Description => "Detects TLS/SSL by record header content type and version";

    public bool Test(ReadOnlyMemory<byte> data)
    {
        // TLS record header is 5 bytes: ContentType(1) + Version(2) + Length(2)
        if (data.Length < 5)
        {
            return false;
        }

        ReadOnlySpan<byte> span = data.Span;
        byte contentType = span[0];
        byte versionMajor = span[1];
        byte versionMinor = span[2];
        ushort length = BinaryPrimitives.ReadUInt16BigEndian(span[3..5]);

        // ContentType: 20=ChangeCipherSpec, 21=Alert, 22=Handshake, 23=ApplicationData
        if (contentType < 20 || contentType > 23)
        {
            return false;
        }

        // Version: major must be 3 (SSLv3, TLS 1.0–1.3), minor 0–4
        if (versionMajor != 3 || versionMinor > 4)
        {
            return false;
        }

        // Length sanity check: TLS record max is 16384 + 2048 (with overhead)
        // Use a generous upper bound to avoid false negatives
        return length > 0 && length <= 18432;
    }
}

/// <summary>
/// Heuristic parser that detects HTTP/2 by the client connection preface.
/// The HTTP/2 connection preface starts with "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n" (24 bytes).
/// Also matches frames with valid HTTP/2 frame header structure.
/// </summary>
internal sealed class Http2HeuristicParser(ProtocolId protocolId) : IHeuristicParser
{
    /// <summary>HTTP/2 client connection preface (first 6 bytes are sufficient to identify).</summary>
    private static ReadOnlySpan<byte> Http2Preface => "PRI * "u8;

    public ProtocolId ProtocolId { get; } = protocolId;
    public string Name => "http2.heuristic";
    public string UiName => "HTTP/2 Heuristic";
    public string? Description => "Detects HTTP/2 by connection preface or frame header";

    public bool Test(ReadOnlyMemory<byte> data)
    {
        if (data.Length < 9)
        {
            return false;
        }

        ReadOnlySpan<byte> span = data.Span;

        // Check for HTTP/2 client connection preface: "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"
        if (span.Length >= Http2Preface.Length && span[..Http2Preface.Length].SequenceEqual(Http2Preface))
        {
            return true;
        }

        // Check for HTTP/2 frame header:
        // 3 bytes length + 1 byte type + 1 byte flags + 4 bytes stream ID = 9 bytes
        // Type must be 0x00–0x09 (DATA through CONTINUATION)
        byte frameType = span[3];
        if (frameType > 9)
        {
            return false;
        }

        // Stream ID: bit 31 is reserved (must be 0)
        if ((span[5] & 0x80) != 0)
        {
            return false;
        }

        // Frame length sanity: max 16384 default, 16MB theoretical
        int frameLength = (span[0] << 16) | (span[1] << 8) | span[2];
        return frameLength <= 16777215 && frameLength + 9 <= data.Length + 9;
    }
}
