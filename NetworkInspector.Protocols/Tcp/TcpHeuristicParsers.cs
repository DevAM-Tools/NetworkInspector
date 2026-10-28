// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tcp;

/// <summary>
/// Heuristic parser that detects HTTP/1.x by inspecting the first line of the TCP payload.
/// The first line (up to the first LF, a trailing CR is dropped) must either start with <c>HTTP/1.</c> (a response status
/// line) or end with an <c>HTTP/1.x</c> version token (a request line such as <c>GET / HTTP/1.1</c>), compared
/// case-insensitively. No method keyword list is checked.
/// </summary>
/// <remarks>
/// The rules follow the established content-based HTTP detection of common packet analyzers, including the rejection of a
/// first line of exactly 8 characters (an inherited rule; the reason for it is not recorded).
/// <b>Thread safety:</b> stateless; safe to call concurrently. Cost: one scan for LF over the payload and two short
/// comparisons, no allocation (spans only).
/// </remarks>
/// <param name="protocolId">Protocol that a match dispatches to.</param>
internal sealed class HttpHeuristicParser(ProtocolId protocolId) : IHeuristicParser
{
    /// <inheritdoc/>
    public ProtocolId ProtocolId { get; } = protocolId;

    /// <inheritdoc/>
    public string Name => "http.heuristic";

    /// <inheritdoc/>
    public string UiName => "HTTP Heuristic";

    /// <inheritdoc/>
    public string? Description => "Detects HTTP/1.x by method or response prefix";

    /// <inheritdoc/>
    public bool Test(ReadOnlyMemory<byte> data)
    {
        // Only a complete first line can be judged: without an LF the line may still be growing, so no match.
        ReadOnlySpan<byte> span = data.Span;
        int lineEnd = span.IndexOf((byte)'\n');
        if (lineEnd < 0)
        {
            return false;
        }

        // Normalize CRLF to the bare line.
        ReadOnlySpan<byte> line = span[..lineEnd];
        if (line.Length > 0 && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        // A first line of exactly 8 characters is rejected: an inherited rule of the established detection, reason not recorded.
        if (line.Length == 8)
        {
            return false;
        }

        return _StartsOrEndsWithHttp1(line);
    }

    /// <summary>
    /// True when the line begins with <c>HTTP/1.</c> (response) or its last 8 characters begin with <c>HTTP/1.</c>
    /// (request line ending in <c>HTTP/1.x</c>). ASCII case-insensitive.
    /// </summary>
    /// <param name="line">First line without CR/LF.</param>
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
/// with a valid TLS version (0x0300–0x0304) and a plausible record length.
/// </summary>
/// <remarks>
/// A 5-byte record header test is weak evidence on its own (any payload starting with these byte values matches), which
/// is why heuristics are only the last resort after port dispatch consumed nothing, and the first match is cached
/// per connection by the caller. <b>Thread safety:</b> stateless; safe to call
/// concurrently. No allocation.
/// </remarks>
/// <param name="protocolId">Protocol that a match dispatches to.</param>
internal sealed class TlsHeuristicParser(ProtocolId protocolId) : IHeuristicParser
{
    /// <inheritdoc/>
    public ProtocolId ProtocolId { get; } = protocolId;

    /// <inheritdoc/>
    public string Name => "tls.heuristic";

    /// <inheritdoc/>
    public string UiName => "TLS/SSL Heuristic";

    /// <inheritdoc/>
    public string? Description => "Detects TLS/SSL by record header content type and version";

    /// <inheritdoc/>
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

        // Length sanity check: a record carries at most 2^14 (16384) bytes of plaintext plus up to 2048 bytes of
        // protocol overhead (TLS 1.2 ciphertext limit), i.e. 18432. The bound is deliberately the largest legal
        // value so valid records are never rejected (false negatives); zero-length records are rejected.
        return length > 0 && length <= 18432;
    }
}

/// <summary>
/// Heuristic parser that detects HTTP/2 by the client connection preface.
/// The HTTP/2 connection preface starts with "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n" (24 bytes).
/// Only the preface is matched; a bare HTTP/2 frame header is not (a bare 9-octet frame header has no fixed byte pattern to match).
/// </summary>
/// <remarks>
/// Consequence: a stream is only recognized when the capture contains the start of the client side. <b>Thread safety:</b>
/// stateless; safe to call concurrently. No allocation (the preface is a UTF-8 literal span in the assembly data).
/// </remarks>
/// <param name="protocolId">Protocol that a match dispatches to.</param>
internal sealed class Http2HeuristicParser(ProtocolId protocolId) : IHeuristicParser
{
    /// <summary>HTTP/2 client connection preface. Only this magic is accepted, not a bare frame header.</summary>
    private static ReadOnlySpan<byte> _Http2Preface => Http2Protocol.ConnectionPreface;

    /// <inheritdoc/>
    public ProtocolId ProtocolId { get; } = protocolId;

    /// <inheritdoc/>
    public string Name => "http2.heuristic";

    /// <inheritdoc/>
    public string UiName => "HTTP/2 Heuristic";

    /// <inheritdoc/>
    public string? Description => "Detects HTTP/2 by the client connection preface";

    /// <inheritdoc/>
    public bool Test(ReadOnlyMemory<byte> data)
    {
        // Exact byte comparison of the first 24 bytes; shorter payloads cannot match.
        ReadOnlySpan<byte> span = data.Span;
        return span.Length >= _Http2Preface.Length && span[.._Http2Preface.Length].SequenceEqual(_Http2Preface);
    }
}
