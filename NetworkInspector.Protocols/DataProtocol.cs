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
/// (single-threaded build phase) and is read-only thereafter, so <see cref="Parse"/> may
/// be invoked concurrently from any number of threads on the same instance without external
/// synchronisation. Per-thread caches (when present) are stored in <c>[ThreadStatic]</c> fields.</para>
/// </remarks>
[Protocol("data", "Data", Description = "Raw data payload")]
public sealed partial class DataProtocol : IProtocol
{
    #region Constants

    /// <summary>Index group for data fields.</summary>
    private const string DataIndexGroup = "data";

    #endregion

    #region Fields

    [BytesField("data", "Data", IndexGroup = DataIndexGroup)]
    private FieldId _ProtocolFieldId;

    [BytesField("data.data", "Data", IndexGroup = DataIndexGroup)]
    private FieldId _DataFieldId;

    [U64Field("data.len", "Length", IndexGroup = DataIndexGroup)]
    private FieldId _LenFieldId;

    /// <summary>
    /// Parses raw data — trivially stores the entire input as a byte field.
    /// </summary>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length == 0)
        {
            return 0;
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_DataGroupId);

        LazyString summary = ZA.Lazy("Data (", data.Length, " bytes)");

        MutField container = parentField.AppendWithCustomText(
            _ProtocolFieldId, FieldValue.NewBytes(data), summary);

        container.Append(_DataFieldId, FieldValue.NewBytes(data));
        container.Append(_LenFieldId, FieldValue.NewU64((ulong)data.Length));

        return data.Length;
    }
    #endregion
}
