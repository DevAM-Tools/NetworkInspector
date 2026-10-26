// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Universal fallback dissector for unrecognized binary payloads.
/// Equivalent to Wireshark's "data" protocol — simply stores raw bytes and length.
/// <para>Field tree structure:</para>
/// <code>
/// data: Data (42 bytes)
/// ├── data.data: [raw bytes]
/// └── data.len: 42
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> / <c>OnStartCustom</c>
/// (single-threaded build phase) and is read-only thereafter, so <see cref="IProtocol.Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// </remarks>
[Protocol("data", "Data", Description = "Raw data payload")]
public sealed partial class DataProtocol : IProtocol
{
    #region Constants

    /// <summary>Index group for data fields.</summary>
    private const string _DataIndexGroup = "data";

    #endregion

    #region Fields

    [BytesField("data", "Data", IndexGroup = _DataIndexGroup)]
    private FieldId _ProtocolFieldId;

    [BytesField("data.data", "Data", IndexGroup = _DataIndexGroup)]
    private FieldId _DataFieldId;

    [U64Field("data.len", "Length", IndexGroup = _DataIndexGroup)]
    private FieldId _LenFieldId;

    /// <summary>
    /// Parses raw data — trivially stores the entire input as a byte field.
    /// </summary>
    /// <param name="parentField">Parent field that receives the <c>data</c> container.</param>
    /// <param name="data">The unrecognized payload; every byte is consumed. Not copied: the fields reference this memory.</param>
    /// <param name="context">Parse context; used only to record index presence.</param>
    /// <returns>The payload length, or 0 (consumed nothing, no error) when <paramref name="data"/> is empty.</returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        // An empty payload has nothing to show: add no empty "data" node and do not index the protocol.
        if (data.Length == 0)
        {
            return 0;
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_DataGroupId);

        // Container and data.data reference the same memory slice (no copy); the container title shows the byte count.
        MutField container = parentField.AppendWithCustomText(
            _ProtocolFieldId, FieldValue.NewBytes(data), "Data (", data.Length, " bytes)");

        container.Append(_DataFieldId, FieldValue.NewBytes(data));
        container.Append(_LenFieldId, FieldValue.NewU64((ulong)data.Length));

        return data.Length;
    }
    #endregion
}
