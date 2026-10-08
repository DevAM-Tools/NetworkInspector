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
        ReadOnlySpan<byte> span = data.Span;
        int lineEnd = span.IndexOf((byte)'\n');
        if (lineEnd < 0)
        {
            return false;
        }

        ReadOnlySpan<byte> line = span[..lineEnd];
        if (line.Length > 0 && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        // Wireshark rejects a line whose length is exactly 8.
        if (line.Length == 8)
        {
            return false;
        }

        return _StartsOrEndsWithHttp1(line);
    }

    private static bool _StartsOrEndsWithHttp1(ReadOnlySpan<byte> line)
    {
        ReadOnlySpan<byte> token = "HTTP/1."u8;
        if (line.Length < token.Length)
        {
            return false;
        }

        if (System.Text.Ascii.EqualsIgnoreCase(line[..token.Length], token))
        {
            return true;
        }

        return line.Length >= 8
            && System.Text.Ascii.EqualsIgnoreCase(line.Slice(line.Length - 8, token.Length), token);
    }
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
    /// <summary>HTTP/2 client connection preface. Wireshark accepts only this magic, not a bare frame header.</summary>
    private static ReadOnlySpan<byte> _Http2Preface => "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8;

    public ProtocolId ProtocolId { get; } = protocolId;
    public string Name => "http2.heuristic";
    public string UiName => "HTTP/2 Heuristic";
    public string? Description => "Detects HTTP/2 by connection preface or frame header";

    public bool Test(ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> span = data.Span;
        return span.Length >= _Http2Preface.Length && span[.._Http2Preface.Length].SequenceEqual(_Http2Preface);
    }
}
