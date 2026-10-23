// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>
/// Reads the JSON files that <c>ni export</c> writes: an array of packet objects whose <c>CH</c> children carry the field tree.
/// Every node has a field id <c>FI</c> and a value <c>VA</c>; the field name <c>NA</c> appears only on the first node of a
/// field, so names are mapped to ids over the whole file first. Immutable after <see cref="Read"/>; thread-safe.
/// </summary>
internal sealed class ExportedPackets
{
    #region Fields

    private readonly Dictionary<string, int> _IdByName;

    #endregion

    #region Lifecycle

    private ExportedPackets(IReadOnlyList<JsonElement> packets, Dictionary<string, int> idByName)
    {
        Packets = packets;
        _IdByName = idByName;
    }

    #endregion

    #region Properties

    /// <summary>The packet objects in file order.</summary>
    internal IReadOnlyList<JsonElement> Packets { get; }

    /// <summary>The number of packets.</summary>
    internal int Count => Packets.Count;

    #endregion

    #region Public API

    /// <summary>Reads a compact JSON export.</summary>
    /// <param name="path">The export file.</param>
    /// <returns>The packets with the name table of the file.</returns>
    internal static ExportedPackets Read(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        List<JsonElement> packets = [.. document.RootElement.EnumerateArray().Select(static packet => packet.Clone())];
        Dictionary<string, int> idByName = new(StringComparer.Ordinal);
        foreach (JsonElement packet in packets)
        {
            _CollectNames(packet, idByName);
        }

        return new ExportedPackets(packets, idByName);
    }

    /// <summary>The numeric value of a field in one packet.</summary>
    /// <param name="index">Zero-based packet position.</param>
    /// <param name="fieldName">The field name, for example <c>tcp.seq</c>.</param>
    /// <returns>The value, or <see langword="null"/> when the packet has no such field or it is not a number.</returns>
    internal long? Number(int index, string fieldName)
    {
        JsonElement? value = Value(index, fieldName);
        return value is { ValueKind: JsonValueKind.Number } number && number.TryGetInt64(out long result)
            ? result
            : null;
    }

    /// <summary>The value of a field in one packet.</summary>
    /// <param name="index">Zero-based packet position.</param>
    /// <param name="fieldName">The field name.</param>
    /// <returns>The value, or <see langword="null"/> when the packet has no such field.</returns>
    internal JsonElement? Value(int index, string fieldName) =>
        _IdByName.TryGetValue(fieldName, out int id)
            ? _FindById(Packets[index], id)
            : null;

    #endregion

    #region Private Helpers

    private static void _CollectNames(JsonElement node, Dictionary<string, int> idByName)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (node.TryGetProperty("NA", out JsonElement name) && node.TryGetProperty("FI", out JsonElement id) && name.GetString() is { } text)
        {
            idByName.TryAdd(text, id.GetInt32());
        }

        if (node.TryGetProperty("CH", out JsonElement children))
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                _CollectNames(child, idByName);
            }
        }
    }

    private static JsonElement? _FindById(JsonElement node, int id)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (node.TryGetProperty("FI", out JsonElement fieldId) && fieldId.GetInt32() == id)
        {
            return node.TryGetProperty("VA", out JsonElement value)
                ? value
                : null;
        }

        if (node.TryGetProperty("CH", out JsonElement children))
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                JsonElement? found = _FindById(child, id);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    #endregion
}
