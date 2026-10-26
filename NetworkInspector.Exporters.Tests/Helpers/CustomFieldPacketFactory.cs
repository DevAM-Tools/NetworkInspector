// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Helpers;

/// <summary>
/// Builds packets with chosen field values on a private stack that holds one test protocol. The standard protocols cannot
/// produce a negative integer or a text with a chosen first character, which several exporter tests need.
/// </summary>
internal sealed class CustomFieldPacketFactory : IDisposable
{
    private readonly Stack _Stack;
    private readonly TestProtocol _Protocol;
    private readonly ProtocolId _ProtocolId;
    private int _NextPacketId;

    /// <summary>Creates a factory with its own stack and a protocol that exposes a signed number and a text field.</summary>
    internal CustomFieldPacketFactory()
    {
        SettingsManager settingsManager = new(); // ownership passes to the stack, which disposes it
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        _Protocol = new();
        _ProtocolId = builder.RegisterProtocol(_Protocol);
        _Protocol.RegisterFields(builder, _ProtocolId);
        _Stack = builder.Build();
    }

    /// <summary>Field id of the signed 64-bit number field.</summary>
    internal FieldId NumberFieldId => _Protocol.NumberFieldId;

    /// <summary>Field id of the text field.</summary>
    internal FieldId TextFieldId => _Protocol.TextFieldId;

    /// <summary>Field id of the bytes field, which aliases the frame data (see <see cref="Create"/>).</summary>
    internal FieldId BytesFieldId => _Protocol.BytesFieldId;

    /// <summary>
    /// Parses one packet whose number field is <paramref name="number"/> and whose text field is
    /// <paramref name="text"/>.
    /// </summary>
    /// <param name="number">Value of the signed number field.</param>
    /// <param name="text">Value of the text field; <see langword="null"/> leaves the field out of the packet.</param>
    /// <param name="info">Packet info text (the CSV <c>Info</c> column).</param>
    /// <param name="frameData">
    /// When given, the frame carries exactly this array (not copied) and the packet gets a bytes field that references it
    /// without copying, like a parser that slices the frame. <see langword="null"/> uses 14 zero bytes and no bytes field.
    /// </param>
    /// <returns>The parsed packet.</returns>
    internal Packet Create(long number, string? text, string info = "", byte[]? frameData = null)
    {
        _Protocol.Number = number;
        _Protocol.Text = text;
        _Protocol.Info = info;
        _Protocol.IncludeBytes = frameData is not null;
        Frame frame = Frame.Create(
            new FrameId(_NextPacketId),
            Timestamp.FromNanos(1_000_000_000L + _NextPacketId),
            frameData ?? new byte[14],
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            _Stack.FrameInterfaceRegistry).Value;
        ParseOptions options = new(firstProtocol: _ProtocolId);
        if (!Packet.TryParse(new PacketId(_NextPacketId), _Stack, frame, in options, out Packet? packet, out ParseFailure failure)
            || packet is null)
        {
            throw new InvalidOperationException(failure.ToString());
        }

        _NextPacketId++;
        return packet;
    }

    /// <inheritdoc/>
    public void Dispose() => _Stack.Dispose();

    /// <summary>Protocol that appends the number and the text of the factory as two fields.</summary>
    private sealed class TestProtocol : IProtocol
    {
        private FieldId _ContainerFieldId;

        /// <inheritdoc/>
        public string Name => "tst";

        /// <inheritdoc/>
        public string UiName => "Test";

        /// <summary>Value written into the number field by the next parse.</summary>
        internal long Number
        {
            get; set;
        }

        /// <summary>Value written into the text field by the next parse.</summary>
        internal string? Text { get; set; }

        /// <summary>Packet info set by the next parse; empty leaves the info unset.</summary>
        internal string Info { get; set; } = string.Empty;

        /// <summary>Whether the next parse appends the bytes field.</summary>
        internal bool IncludeBytes
        {
            get; set;
        }

        /// <summary>Id of the number field.</summary>
        internal FieldId NumberFieldId
        {
            get; private set;
        }

        /// <summary>Id of the text field.</summary>
        internal FieldId TextFieldId
        {
            get; private set;
        }

        /// <summary>Id of the bytes field.</summary>
        internal FieldId BytesFieldId
        {
            get; private set;
        }

        /// <summary>Registers the container and the two fields.</summary>
        internal void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _ContainerFieldId = builder.RegisterField(protocolId, "tst", "Test", FieldType.None);
            NumberFieldId = builder.RegisterField(protocolId, "tst.number", "Number", FieldType.I64);
            TextFieldId = builder.RegisterField(protocolId, "tst.text", "Text", FieldType.String);
            BytesFieldId = builder.RegisterField(protocolId, "tst.bytes", "Bytes", FieldType.Bytes);
        }

        /// <inheritdoc/>
        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            long number = Number;
            string? text = Text;
            string info = Info;
            bool includeBytes = IncludeBytes;
            FieldId bytesFieldId = BytesFieldId;
            ReadOnlyMemory<byte> payload = data;
            FieldId numberFieldId = NumberFieldId;
            FieldId textFieldId = TextFieldId;
            if (info.Length > 0)
            {
                parentField.SetPacketInfo(new LazyString(info));
            }

            parentField.AppendLazy(_ContainerFieldId, FieldValue.None, (in container) =>
            {
                container.Append(numberFieldId, FieldValue.NewI64(number));
                if (text is not null)
                {
                    container.Append(textFieldId, FieldValue.NewString(text));
                }

                if (includeBytes)
                {
                    container.Append(bytesFieldId, FieldValue.NewBytes(payload));
                }

                return 0;
            });
            return data.Length;
        }
    }
}
