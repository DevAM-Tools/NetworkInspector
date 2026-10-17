// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Columnar;

/// <summary>
/// Test capture for the columnar catalog: a dedicated stack with one test protocol that registers a field with a
/// description and a field without one, plus packets parsed through it. The standard protocols carry no field
/// descriptions, so this is the only way to prove a non-null <c>description</c> reaches the catalog.
/// <para>
/// The stack stays alive until <see cref="Dispose"/>, because exporters read <see cref="Packet.Stack"/> while exporting.
/// </para>
/// <para><b>Thread safety:</b> not shared between tests; each test creates and disposes its own capture.</para>
/// </summary>
internal sealed class DescribedFieldCapture : IDisposable
{
    #region Constants

    /// <summary>Machine name of the test protocol.</summary>
    internal const string ProtocolName = "test.catalog";

    /// <summary>Display name of the test protocol.</summary>
    internal const string ProtocolUiName = "Catalog Test";

    /// <summary>Name of the field registered with <see cref="FieldDescription"/>.</summary>
    internal const string DescribedFieldName = "test.catalog.described";

    /// <summary>Name of the field registered without a description.</summary>
    internal const string PlainFieldName = "test.catalog.plain";

    /// <summary>Description of <see cref="DescribedFieldName"/>.</summary>
    internal const string FieldDescription = "Catalog test field with a description";

    #endregion

    #region Constructor

    private DescribedFieldCapture(Stack stack, Packet[] packets)
    {
        Stack = stack;
        Packets = packets;
    }

    #endregion

    #region Properties

    /// <summary>The stack that parsed <see cref="Packets"/>; owned by this capture.</summary>
    internal Stack Stack { get; }

    /// <summary>Fully materialized packets, each carrying one described and one plain field.</summary>
    internal Packet[] Packets { get; }

    #endregion

    #region Factory

    /// <summary>Builds the test stack and parses <paramref name="packetCount"/> packets through it.</summary>
    /// <param name="packetCount">Number of packets; must be greater than zero.</param>
    /// <returns>The capture; dispose it after the export finished.</returns>
    internal static DescribedFieldCapture Create(int packetCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(packetCount);

        // Build the stack: standard protocols provide the frame dispatch table, the test protocol is mapped to
        // LinkType.Null, which no standard protocol uses.
        SettingsManager? settingsManager = new();
        Stack stack;
        try
        {
            FrameInterfaceRegistry builderRegistry = new();
            StackBuilder builder = new(settingsManager, builderRegistry);
            builder.RegisterStandardProtocols();
            builder.RegisterProtocol<CatalogTestProtocol>(
                new(),
                (b, id, _) =>
                {
                    b.RegisterField(id, DescribedFieldName, "Described", FieldType.U64, FieldDescription);
                    b.RegisterField(id, PlainFieldName, "Plain", FieldType.U64);
                    b.RegisterParserInU64TableByName(FrameProtocol.LinkTypeTableName, (ulong)LinkType.Null, id);
                });
            stack = builder.Build();
            settingsManager = null; // ownership transferred to the stack
        }
        finally
        {
            settingsManager?.Dispose();
        }

        // Parse the packets; on failure the stack is disposed so no native or settings resources leak.
        try
        {
            FrameInterfaceRegistry registry = stack.FrameInterfaceRegistry;
            FrameSourceId sourceId = registry.RegisterSource(TestHarness.CreateNullFrameSource());
            FrameInterfaceId interfaceId = registry.Register(sourceId, "test_catalog", null, LinkType.Null);
            Packet[] packets = new Packet[packetCount];
            ParseOptions options = new();
            for (int i = 0; i < packetCount; i++)
            {
                Frame frame = Frame.Create(
                    new FrameId(i + 1),
                    Timestamp.FromNanos(1_000_000L * (i + 1)),
                    new byte[] { 0, 0, 0, 0 },
                    LinkType.Null,
                    interfaceId,
                    registry).Value;
                if (!Packet.TryParse(new PacketId(i), stack, frame, in options, out Packet? packet, out ParseFailure failure)
                    || packet is null)
                {
                    throw new InvalidOperationException(failure.ToString());
                }
                packet.MaterializeAll();
                packets[i] = packet;
            }
            return new DescribedFieldCapture(stack, packets);
        }
        catch
        {
            stack.Dispose();
            throw;
        }
    }

    #endregion

    #region IDisposable

    /// <inheritdoc/>
    public void Dispose() => Stack.Dispose();

    #endregion

    #region Test protocol

    /// <summary>
    /// Appends one <see cref="DescribedFieldName"/> and one <see cref="PlainFieldName"/> U64 value per packet.
    /// <para><b>Thread safety:</b> immutable after <see cref="OnStart"/>; <see cref="Parse"/> may run concurrently.</para>
    /// </summary>
    private sealed class CatalogTestProtocol : IProtocol
    {
        private FieldId _DescribedFieldId;
        private FieldId _PlainFieldId;

        /// <inheritdoc/>
        public string Name => ProtocolName;

        /// <inheritdoc/>
        public string UiName => ProtocolUiName;

        /// <inheritdoc/>
        public void OnStart(Stack stack)
        {
            // Fields were registered in the RegisterProtocol callback; resolve the ids once.
            _DescribedFieldId = stack.GetFieldId(DescribedFieldName)
                ?? throw new InvalidOperationException("Described test field not registered.");
            _PlainFieldId = stack.GetFieldId(PlainFieldName)
                ?? throw new InvalidOperationException("Plain test field not registered.");
        }

        /// <inheritdoc/>
        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            parentField.Append(_DescribedFieldId, FieldValue.NewU64(7));
            parentField.Append(_PlainFieldId, FieldValue.NewU64(9));
            return 0;
        }
    }

    #endregion
}
