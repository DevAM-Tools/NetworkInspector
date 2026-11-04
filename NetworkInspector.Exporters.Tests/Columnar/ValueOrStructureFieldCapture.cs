// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Columnar;

/// <summary>
/// Test capture for the catalog rule of the columnar exporters: a dedicated stack with one test protocol that appends the same
/// field once with a value and once as a node without a value (a structure node), chosen by the first frame byte. Two batches
/// that each saw the id in one of the two forms disagree about its catalog row, which is the case the rule must settle.
/// <para>
/// The stack stays alive until <see cref="Dispose"/>, because exporters read <see cref="Packet.Stack"/> while exporting.
/// </para>
/// <para><b>Thread safety:</b> not shared between tests; each test creates and disposes its own capture.</para>
/// </summary>
internal sealed class ValueOrStructureFieldCapture : IDisposable
{
    #region Constants

    /// <summary>Machine name of the test protocol.</summary>
    internal const string ProtocolName = "test.valueorstructure";

    /// <summary>Name of the field that carries a value in the first packet and no value in the second.</summary>
    internal const string FieldName = "test.valueorstructure.field";

    #endregion

    #region Constructor

    private ValueOrStructureFieldCapture(Stack stack, Packet[] packets)
    {
        Stack = stack;
        Packets = packets;
    }

    #endregion

    #region Properties

    /// <summary>The stack that parsed <see cref="Packets"/>; owned by this capture.</summary>
    internal Stack Stack { get; }

    /// <summary>
    /// Two fully materialized packets: the first carries <see cref="FieldName"/> with a value, the second carries the same
    /// field without a value.
    /// </summary>
    internal Packet[] Packets { get; }

    #endregion

    #region Factory

    /// <summary>Builds the test stack and parses the two packets through it.</summary>
    /// <returns>The capture; dispose it after the export finished.</returns>
    internal static ValueOrStructureFieldCapture Create()
    {
        // The standard protocols provide the frame dispatch table; the test protocol is mapped to LinkType.Null, which no
        // standard protocol uses.
        SettingsManager? settingsManager = new();
        Stack stack;
        try
        {
            FrameInterfaceRegistry builderRegistry = new();
            StackBuilder builder = new(settingsManager, builderRegistry);
            builder.RegisterStandardProtocols();
            builder.RegisterProtocol<ValueOrStructureProtocol>(
                new(),
                (b, id, _) =>
                {
                    b.RegisterField(id, FieldName, "Value or structure", FieldType.U64);
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
            FrameInterfaceId interfaceId = registry.Register(sourceId, "test_value_or_structure", null, LinkType.Null);
            Packet[] packets = new Packet[2];
            ParseOptions options = new();
            for (int i = 0; i < packets.Length; i++)
            {
                // First byte 1: the field carries a value; first byte 0: the field is appended without one.
                byte marker = i == 0
                    ? (byte)1
                    : (byte)0;
                Frame frame = Frame.Create(
                    i + 1,
                    Timestamp.FromNanos(1_000_000L * (i + 1)),
                    new byte[] { marker, 0, 0, 0 },
                    LinkType.Null,
                    interfaceId,
                    registry).Value;
                if (!Packet.TryParse(i, stack, frame, in options, out Packet? packet, out ParseFailure failure)
                    || packet is null)
                {
                    throw new InvalidOperationException(failure.ToString());
                }
                packet.MaterializeAll();
                packets[i] = packet;
            }
            return new ValueOrStructureFieldCapture(stack, packets);
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
    /// Appends <see cref="FieldName"/> with the value 7 when the first frame byte is 1 and without a value otherwise.
    /// <para><b>Thread safety:</b> immutable after <see cref="OnStart"/>; <see cref="Parse"/> may run concurrently.</para>
    /// </summary>
    private sealed class ValueOrStructureProtocol : IProtocol
    {
        /// <summary>Id of <see cref="FieldName"/>, resolved once in <see cref="OnStart"/>.</summary>
        private FieldId _FieldId;

        /// <inheritdoc/>
        public string Name => ProtocolName;

        /// <inheritdoc/>
        public string UiName => "Value Or Structure Test";

        /// <inheritdoc/>
        public void OnStart(Stack stack)
        {
            // The field was registered in the RegisterProtocol callback; resolve the id once.
            _FieldId = stack.GetFieldId(FieldName)
                ?? throw new InvalidOperationException("Value-or-structure test field not registered.");
        }

        /// <inheritdoc/>
        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            FieldValue value = data.Span[0] == 1
                ? FieldValue.NewU64(7)
                : FieldValue.None;
            parentField.Append(_FieldId, value);
            return 0;
        }
    }

    #endregion
}
