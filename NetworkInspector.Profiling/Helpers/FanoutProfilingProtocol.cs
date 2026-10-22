// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Helpers;

/// <summary>
/// Synthetic protocol for the field-storage scenarios. On link type <see cref="LinkType.User0"/> it appends one
/// container with N U64 children; N is read big-endian from the first two frame bytes. It yields large trees (hundreds
/// of slots) without depending on a real protocol's field layout.
/// <para><b>Thread safety:</b> immutable after <see cref="Register"/>; <see cref="Parse"/> is safe from any thread.</para>
/// </summary>
internal sealed class FanoutProfilingProtocol : IProtocol
{
    #region Constants

    /// <summary>Largest accepted item count (the mixed scenario uses 600).</summary>
    internal const int MaxItems = 1_000;

    /// <summary>Frame size: one big-endian <see cref="ushort"/> item count.</summary>
    internal const int FrameSize = 2;

    #endregion

    #region Fields

    private FieldId _ContainerId;
    private FieldId _ItemId;

    #endregion

    #region IProtocol

    /// <inheritdoc/>
    public string Name => "profiling.fanout";

    /// <inheritdoc/>
    public string UiName => "Profiling Fanout";

    /// <inheritdoc/>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        // Synthetic frames come from SampleFrames only; still reject a short frame or a count above MaxItems.
        if (data.Length < FrameSize)
        {
            return ParseError.InvalidData(Name, "Fanout frame is shorter than its 2-byte item count.");
        }

        int items = BinaryPrimitives.ReadUInt16BigEndian(data.Span);
        if (items > MaxItems)
        {
            return ParseError.InvalidData(Name, "Fanout item count is above MaxItems.");
        }

        // One container plus its items: items + 1 slots.
        MutField container = parentField.Append(_ContainerId, FieldValue.None);
        for (int i = 0; i < items; i++)
        {
            _ = container.Append(_ItemId, FieldValue.NewU64((ulong)i));
        }

        return data.Length;
    }

    #endregion

    #region Registration

    /// <summary>Registers the protocol, its two fields, and its <see cref="LinkType.User0"/> link-type entry.</summary>
    /// <param name="builder">Builder that already registered <see cref="FrameProtocol"/> (it owns the link-type table).</param>
    internal static void Register(IStackBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        FanoutProfilingProtocol protocol = new();
        ProtocolId id = builder.RegisterProtocol(protocol);
        protocol._ContainerId = builder.RegisterField(id, "profiling.fanout", "Fanout", FieldType.None);
        protocol._ItemId = builder.RegisterField(id, "profiling.fanout.item", "Item", FieldType.U64);
        builder.RegisterParserInU64TableByName(FrameProtocol.LinkTypeTableName, (ulong)LinkType.User0, id);
    }

    #endregion
}
