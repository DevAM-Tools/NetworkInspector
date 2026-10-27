// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace Sample.Protocol;

/// <summary>
/// The <c>sample</c> protocol: a 14-byte payload (magic <c>NIS1</c>, counter, gear, speed) that the stack finds by UDP port
/// 40000 or, over TCP, through the <see cref="SampleHeuristicParser"/>. It is a hand-written <see cref="IProtocol"/> to show
/// the plain contract; protocols inside NetworkInspector use generated registration.
/// <para>
/// Field tree: <c>sample</c> with the children <c>sample.counter</c>, <c>sample.gear</c>, and <c>sample.speed</c> (all
/// unsigned 64-bit integers).
/// </para>
/// <para>
/// Thread safety: the protocol is stateless after <see cref="RegisterFields"/>; the field ids are written once while the
/// stack is built and only read afterwards, so a stack may parse on any thread and re-parse the same bytes at any time.
/// </para>
/// </summary>
internal sealed class SampleProtocol : IProtocol
{
    #region Fields

    private ProtocolId _ProtocolId;

    private FieldId _ContainerFieldId;

    private FieldId _CounterFieldId;

    private FieldId _GearFieldId;

    private FieldId _SpeedFieldId;

    #endregion

    #region IProtocol

    /// <inheritdoc/>
    public string Name => SampleWire.ProtocolName;

    /// <inheritdoc/>
    public string UiName => "Sample protocol";

    /// <inheritdoc/>
    public string? Description => "Sample payload with a NIS1 magic, a counter, a gear, and a speed.";

    /// <inheritdoc/>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        // Stateless: first parse and replay decode the same bytes, so any thread may re-parse.
        ReadOnlySpan<byte> span = data.Span;
        if (span.Length < SampleWire.PayloadLength)
        {
            return ParseError.InsufficientDataWithInfo(SampleWire.ProtocolName, SampleWire.PayloadLength, (ulong)span.Length);
        }

        // A port registration cannot tell a sample payload from other traffic on the port, so the magic is checked here.
        if (!span[..SampleWire.CounterOffset].SequenceEqual(SampleWire.MagicBytes))
        {
            return ParseError.InvalidData(SampleWire.ProtocolName, "Magic is not NIS1.");
        }

        // Wire layout: magic 4, counter 8 (big endian), gear 1, speed 1.
        ulong counter = BinaryPrimitives.ReadUInt64BigEndian(span[SampleWire.CounterOffset..]);
        byte gear = span[SampleWire.GearOffset];
        byte speed = span[SampleWire.SpeedOffset];

        context.RecordProtocolPresence(_ProtocolId);

        MutField container = parentField.Append(_ContainerFieldId, FieldValue.None);
        container.Append(_CounterFieldId, FieldValue.NewU64(counter));
        container.Append(_GearFieldId, FieldValue.NewU64(gear));
        container.Append(_SpeedFieldId, FieldValue.NewU64(speed));

        // The text is formatted when somebody reads it, not while parsing: nothing is allocated per packet for the info.
        container.SetPacketInfo(LazyString.FormatLazy(
            (counter, gear, speed),
            static s => string.Create(CultureInfo.InvariantCulture, $"Sample counter={s.counter} gear={s.gear} speed={s.speed}")));
        return SampleWire.PayloadLength;
    }

    #endregion

    #region Internal API

    /// <summary>Registers the fields of the protocol and keeps their ids; called once, right after the protocol registration.</summary>
    /// <param name="stack">The builder that registered this protocol.</param>
    /// <param name="protocolId">The id the builder assigned to this protocol.</param>
    internal void RegisterFields(IStackBuilder stack, ProtocolId protocolId)
    {
        _ProtocolId = protocolId;
        _ContainerFieldId = stack.RegisterField(protocolId, SampleWire.ProtocolName, "Sample", FieldType.None,
            "Container of the sample payload.");
        _CounterFieldId = stack.RegisterField(protocolId, "sample.counter", "Counter", FieldType.U64,
            "Frame counter of the sample payload.");
        _GearFieldId = stack.RegisterField(protocolId, "sample.gear", "Gear", FieldType.U64,
            "Gear 0-4 (P, R, N, D, S).");
        _SpeedFieldId = stack.RegisterField(protocolId, "sample.speed", "Speed", FieldType.U64,
            "Speed 0-253; 254 means error, 255 means signal not available.");
    }

    #endregion
}
