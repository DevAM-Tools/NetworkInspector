// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Helpers;

/// <summary>
/// Builds packets that carry exactly the additional buffers a test names: none, one, several, an empty one. The factory has its own
/// stack with one probe protocol that binds the requested byte arrays to the packet (<c>MutField.TryBindBuffer</c>, the call every
/// reassembling protocol makes) and appends one field, so a packet exports like a real one. The standard protocols cannot produce
/// an empty buffer or a chosen buffer count, which the order and boundary tests need.
/// </summary>
/// <remarks>Thread safety: not thread-safe; one test owns one factory.</remarks>
internal sealed class BufferProbePacketFactory : IDisposable
{
    #region Factory state

    /// <summary>The factory's own stack, disposed with the factory.</summary>
    private readonly Stack _Stack;

    /// <summary>The probe protocol that binds the requested buffers.</summary>
    private readonly ProbeProtocol _Protocol;

    /// <summary>The id of the probe protocol in <see cref="_Stack"/>; the first protocol of every parse.</summary>
    private readonly ProtocolId _ProtocolId;

    /// <summary>The id of the next packet (and frame) the factory creates.</summary>
    private int _NextPacketId;

    /// <summary>Creates a factory with its own stack and the probe protocol.</summary>
    internal BufferProbePacketFactory()
    {
        SettingsManager settingsManager = new(); // ownership passes to the stack, which disposes it
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        _Protocol = new();
        _ProtocolId = builder.RegisterProtocol(_Protocol);
        _Protocol.RegisterFields(builder, _ProtocolId);
        _Stack = builder.Build();
    }

    /// <summary>The factory's own stack.</summary>
    internal Stack Stack => _Stack;

    /// <summary>
    /// Whether the next packets get a custom text whose factory throws when the text is read, so adding such a packet to a batch fails
    /// after the batch has already copied the packet's buffers (the copy comes before the field tree).
    /// </summary>
    internal bool FaultyText
    {
        get => _Protocol.FaultyText;
        set => _Protocol.FaultyText = value;
    }

    /// <inheritdoc/>
    public void Dispose() => _Stack.Dispose();

    #endregion

    #region Building packets

    /// <summary>
    /// Parses one probe frame with a field tree; the packet carries the given buffers as its additional buffers 1, 2, 3, … in the
    /// order given.
    /// </summary>
    /// <param name="buffers">The buffers to bind, in index order; an empty array is a bound empty buffer. None gives a packet that has only its frame.</param>
    /// <returns>The parsed packet; its id is the next number of the factory (0, 1, 2, …).</returns>
    /// <exception cref="InvalidOperationException">The frame does not parse.</exception>
    internal Packet CreatePacket(params byte[][] buffers)
    {
        _Protocol.Buffers = buffers;
        int id = _NextPacketId++;
        Frame frame = Frame.Create(
            id,
            Timestamp.FromNanos(1_000_000_000L + id),
            new byte[14],
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            _Stack.FrameInterfaceRegistry).Value;
        ParseOptions options = new(firstProtocol: _ProtocolId);
        if (!Packet.TryParse(id, _Stack, frame, in options, out Packet? packet, out ParseFailure failure) || packet is null)
        {
            throw new InvalidOperationException(failure.ToString());
        }

        return packet;
    }

    #endregion

    #region Probe protocol

    /// <summary>The probe protocol: appends one field and binds the buffers the factory asks for.</summary>
    private sealed class ProbeProtocol : IProtocol
    {
        /// <summary>The layer container field.</summary>
        private FieldId _LayerId;

        /// <summary>The value field that holds the number of bound buffers.</summary>
        private FieldId _ValueId;

        /// <summary>The buffers the next parse binds, in index order.</summary>
        internal byte[][] Buffers { get; set; } = [];

        /// <summary>Whether the value field gets a custom text whose factory throws when it is read.</summary>
        internal bool FaultyText { get; set; }

        /// <inheritdoc/>
        public string Name => "bfp";

        /// <inheritdoc/>
        public string UiName => "Probe Layer";

        /// <summary>Registers the layer container and the value field.</summary>
        /// <param name="builder">The builder of the factory's stack.</param>
        /// <param name="protocolId">The id of this protocol.</param>
        internal void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _LayerId = builder.RegisterField(protocolId, "bfp", "Probe Layer", FieldType.None);
            _ValueId = builder.RegisterField(protocolId, "bfp.count", "Probe Count", FieldType.U64);
        }

        /// <inheritdoc/>
        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            MutField layer = parentField.Append(_LayerId, FieldValue.None);
            FieldValue count = FieldValue.NewU64(checked((ulong)Buffers.Length));
            if (FaultyText)
            {
                // A plugin dissector whose text factory throws when the text is first read.
                LazyString faulty = LazyString.Lazy(static () => throw new InvalidOperationException("text boom"));
                _ = layer.AppendWithCustomText(_ValueId, count, faulty);
            }
            else
            {
                _ = layer.Append(_ValueId, count);
            }

            // The same call a reassembling protocol makes: the bytes become additional buffers 1, 2, 3, … in call order.
            foreach (byte[] buffer in Buffers)
            {
                _ = parentField.TryBindBuffer(buffer, out _, out _);
            }

            return data.Length;
        }
    }

    #endregion
}
