// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// IEEE 802.2 LLC (Logical Link Control) protocol parser with SNAP extension.
/// Handles the sub-layer between IEEE 802.3 Ethernet and upper protocols.
/// <para>Frame types and control field widths (per IEEE 802.2):</para>
/// <code>
/// U-frame (bits 1:0 = 11): 1-byte control  → used for SNAP (UI, 0x03)
/// S-frame (bits 1:0 = 10): 2-byte control  → supervisory (RR, RNR, REJ)
/// I-frame (bit 0 = 0):     2-byte control  → information transfer
/// </code>
/// <para>Field tree structure:</para>
/// <code>
/// llc: LLC (SNAP), Type: IPv4 (0x0800)
/// ├── llc.dsap: 0xaa (SNAP)
/// ├── llc.ssap: 0xaa (SNAP)
/// ├── llc.control: 0x03
/// ├── llc.oui: 00:00:00        [only with SNAP]
/// └── llc.type: 0x0800 (IPv4)  [only with SNAP]
/// </code>
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Not thread-safe; designed for single-threaded use within a
/// protocol stack. Each <see cref="Stack"/> instance is owned by exactly one parsing thread.</para>
/// </remarks>
[Protocol("llc", "Logical-Link Control", Description = "IEEE 802.2 LLC")]
[RegisterAtTable(EthernetProtocol.Ieee8023TableName, Ieee8023Key)]
public sealed partial class LlcProtocol : IProtocol
{
    /// <summary>Catch-all key for IEEE 802.3 frames dispatched from Ethernet.</summary>
    public const ulong Ieee8023Key = 1;

    /// <summary>DSAP/SSAP value indicating a SNAP extension follows.</summary>
    private const byte _SnapSap = 0xAA;

    /// <summary>Minimum LLC header size (DSAP + SSAP + Control).</summary>
    private const int _MinHeaderSize = 3;

    /// <summary>SNAP extension size (3 bytes OUI + 2 bytes Type).</summary>
    private const int _SnapSize = 5;

    /// <summary>Dispatch table name for LLC DSAP-based protocol lookup.</summary>
    public const string DsapTableName = "llc.dsap";

    #region Index Group Constants

    /// <summary>Index group for always-present LLC fields.</summary>
    private const string _LlcIndexGroup = "llc";

    /// <summary>Index group for optional SNAP fields (only when DSAP=0xAA).</summary>
    private const string _SnapIndexGroup = "llc.snap";

    #endregion

    #region Fields (always present)

    [BytesField("llc", "LLC", IndexGroup = _LlcIndexGroup)]
    private FieldId _ProtocolFieldId;

    [U64Field("llc.dsap", "DSAP", IndexGroup = _LlcIndexGroup)]
    private FieldId _DsapFieldId;

    [U64Field("llc.ssap", "SSAP", IndexGroup = _LlcIndexGroup)]
    private FieldId _SsapFieldId;

    [U64Field("llc.control", "Control", IndexGroup = _LlcIndexGroup)]
    private FieldId _ControlFieldId;

    #endregion

    #region SNAP-specific fields (optional)

    [BytesField("llc.oui", "Organization Code", IndexGroup = _SnapIndexGroup)]
    private FieldId _OuiFieldId;

    [U64Field("llc.type", "Type", IndexGroup = _SnapIndexGroup)]
    private FieldId _TypeFieldId;

    // Reuse Ethernet's type table for SNAP dispatch
    [UsesTable(EthernetProtocol.EtherTypeTableName)]
    private ProtocolTableId _EtherTypeTableId;

    // Own DSAP table for non-SNAP SAPs (STP, NetBIOS, IPX)
    [ProtocolTableU64(DsapTableName, "LLC DSAP")]
    private ProtocolTableId _DsapTableId;

    // Sparse dispatch cache for EtherType (SNAP) dispatch
    private (ulong Key, ProtocolId Id)[] _EtherTypeSparseCache = [];

    partial void _OnStartCustom(Stack stack) =>
        _EtherTypeSparseCache = stack.BuildU64SparseIdCache(_EtherTypeTableId);

    /// <summary>
    /// Parses a Llc protocol unit from the supplied <paramref name="data"/> buffer,
    /// appending decoded fields under <paramref name="parentField"/> and dispatching any
    /// payload via the surrounding <paramref name="context"/>.
    /// </summary>
    /// <param name="parentField">Parent field that receives the decoded protocol container and child fields.</param>
    /// <param name="data">Raw protocol bytes starting at this protocol's first header byte.</param>
    /// <param name="context">Owning stack used to dispatch the next-protocol payload (when applicable).</param>
    /// <returns>Number of bytes consumed, or a <see cref="ParseError"/> describing the failure.</returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length < _MinHeaderSize)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, _MinHeaderSize, (ulong)data.Length);
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_LlcGroupId);

        ReadOnlySpan<byte> span = data.Span;
        byte dsap = span[0];
        byte ssap = span[1];
        byte control = span[2];

        // IEEE 802.2 LLC frame type, determined by the low 2 bits of the first control byte:
        //   U-frame: bits 1:0 = 11 → 1-byte control field (unnumbered commands/responses)
        //   S-frame: bits 1:0 = 10 → 2-byte control field (supervisory)
        //   I-frame: bit 0 = 0     → 2-byte control field (information)
        // SNAP always uses the UI (0x03) U-frame, so SNAP detection is not affected.
        bool isUFrame = (control & 0x03) == 0x03;

        // 2-byte control (I/S frames) requires one additional byte beyond _MinHeaderSize.
        if (!isUFrame && data.Length < _MinHeaderSize + 1)
        {
            return ParseError.InsufficientDataWithInfo(ProtocolName, _MinHeaderSize + 1, (ulong)data.Length);
        }

        // Full control field value: 1 byte for U-frames, 2 bytes (LE) for I/S-frames.
        ushort controlValue = isUFrame ? control : BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);

        // LLC header occupies 3 bytes for U-frames, 4 bytes for I/S-frames.
        int llcHeaderSize = isUFrame ? _MinHeaderSize : _MinHeaderSize + 1;

        bool isSnap = dsap == _SnapSap && ssap == _SnapSap && control == 0x03;

        int headerSize = llcHeaderSize;
        ushort snapType = 0;

        if (isSnap)
        {
            // SNAP requires 3 (LLC U-frame) + 5 (SNAP) = 8 bytes minimum
            if (data.Length < _MinHeaderSize + _SnapSize)
            {
                return ParseError.InsufficientDataWithInfo(
                    ProtocolName, _MinHeaderSize + _SnapSize, (ulong)data.Length);
            }

            context.RecordGroupPresence(_LlcSnapGroupId);
            snapType = BinaryPrimitives.ReadUInt16BigEndian(span[6..]);
            headerSize = _MinHeaderSize + _SnapSize;
        }

        // Build summary text
        LazyString summary = isSnap
            ? ZA.Lazy("LLC (SNAP), Type: ", DisplayTables.GetEtherTypeDisplayText(snapType))
            : ZA.Lazy("LLC, DSAP: ", DisplayTables.GetLlcSapDisplayText(dsap),
                       ", SSAP: ", DisplayTables.GetLlcSapDisplayText(ssap));

        // Append protocol container and fields
        FieldValue headerValue = FieldValue.NewBytes(data[..headerSize]);
        MutField container = parentField.AppendWithCustomText(_ProtocolFieldId, headerValue, summary);

        string dsapText = DisplayTables.GetLlcSapDisplayText(dsap);
        container.AppendWithCustomText(_DsapFieldId, FieldValue.NewU64(dsap), dsapText);

        string ssapText = DisplayTables.GetLlcSapDisplayText(ssap);
        container.AppendWithCustomText(_SsapFieldId, FieldValue.NewU64(ssap), ssapText);

        container.Append(_ControlFieldId, FieldValue.NewU64(controlValue));

        if (isSnap)
        {
            // OUI: 3 bytes at offset 3
            container.Append(_OuiFieldId, FieldValue.NewBytes(data[3..6]));

            string etypeText = DisplayTables.GetEtherTypeDisplayText(snapType);
            container.AppendWithCustomText(_TypeFieldId, FieldValue.NewU64(snapType), etypeText);
        }

        // Dispatch to next protocol
        ReadOnlyMemory<byte> payload = data[headerSize..];
        if (isSnap)
        {
            // SNAP with OUI 0x000000: dispatch via EtherType table.
            // Non-zero OUI means vendor-specific protocol — the type field is not an EtherType,
            // so we skip dispatch (no vendor-specific SAP tables are registered).
            byte oui0 = span[3];
            byte oui1 = span[4];
            byte oui2 = span[5];
            if (oui0 == 0 && oui1 == 0 && oui2 == 0)
            {
                ParseResult result = _DispatchEtherType(in parentField, snapType, payload, in context);
                if (result.TryPropagateError(out ParseResult error))
                {
                    return error;
                }
            }
        }
        else
        {
            // Non-SNAP: dispatch via DSAP table
            // Mask out the low bit (I/G bit for DSAP) for dispatch
            ParseResult result = parentField.TryCallNextProtocolU64(
                _DsapTableId, (ulong)(dsap & 0xFE), payload, in context);
            if (result.TryPropagateError(out ParseResult error))
            {
                return error;
            }
        }

        return data.Length;
    }

    /// <summary>
    /// Dispatches to the next protocol by EtherType using the sparse cache.
    /// Falls back to full table dispatch for unknown EtherTypes.
    /// </summary>
    private ParseResult _DispatchEtherType(
        in MutField parentField, ulong etherType, ReadOnlyMemory<byte> payload, in ParseContext context)
    {
        foreach ((ulong key, ProtocolId id) in _EtherTypeSparseCache)
        {
            if (key == etherType)
            {
                return parentField.CallProtocol(id, payload, in context);
            }
        }

        return parentField.TryCallNextProtocolU64(_EtherTypeTableId, etherType, payload, in context);
    }
    #endregion
}
