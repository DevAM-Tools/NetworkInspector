// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Exporters.Tests.Verification;

/// <summary>
/// Verifies JSON exporter output by parsing it with <see cref="JsonDocument"/>.
/// Validates structural integrity and element types.
/// </summary>
internal sealed class JsonVerifier
{
    /// <summary>The parsed JSON document root element.</summary>
    internal JsonElement Root
    {
        get;
    }

    /// <summary>The number of packets (top-level array elements).</summary>
    internal int PacketCount
    {
        get;
    }

    /// <summary>The underlying <see cref="JsonDocument"/> (must be disposed).</summary>
    private readonly JsonDocument _Document;

    /// <summary>Creates a verifier from a parsed document.</summary>
    private JsonVerifier(JsonDocument document)
    {
        _Document = document;
        Root = document.RootElement;
        PacketCount = Root.ValueKind == JsonValueKind.Array ? Root.GetArrayLength() : 0;
    }

    /// <summary>
    /// Opens a JSON file and parses it. Throws on invalid JSON.
    /// </summary>
    internal static JsonVerifier Open(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        return FromBytes(data);
    }

    /// <summary>
    /// Parses JSON from a byte array. Throws on invalid JSON.
    /// </summary>
    internal static JsonVerifier FromBytes(byte[] data)
    {
        JsonDocument doc = JsonDocument.Parse(data);
        return new JsonVerifier(doc);
    }

    /// <summary>
    /// Parses JSON from a stream. Throws on invalid JSON.
    /// </summary>
    internal static JsonVerifier FromStream(Stream stream)
    {
        stream.Position = 0;
        JsonDocument doc = JsonDocument.Parse(stream);
        return new JsonVerifier(doc);
    }

    /// <summary>
    /// Verifies the top-level structure is a JSON array.
    /// </summary>
    internal bool IsArray => Root.ValueKind == JsonValueKind.Array;

    /// <summary>
    /// Returns the i-th packet element from the root array.
    /// </summary>
    internal JsonElement GetPacket(int index) => Root[index];

    /// <summary>Disposes the underlying JSON document.</summary>
    internal void Dispose() => _Document.Dispose();
}
