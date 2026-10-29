// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.Infrastructure.SomeIp;

/// <summary>
/// A stand-in for a protocol that decrypts a transport (TLS or DTLS) and hands the plaintext to an application protocol. It treats
/// its whole payload as plaintext: it publishes the plaintext marker for the packet, calls SOME/IP directly (no dispatch key, as
/// the decrypting protocol does), and withdraws the marker in a <c>finally</c>.
/// <para>
/// Why a stand-in: the reader side of the hand-off belongs to the SOME/IP parser and is tested here; the publisher side belongs to
/// the TLS and DTLS parsers and is delivered with their own rework. The stand-in follows the contract those parsers must
/// follow, so the test shows what the SOME/IP side requires of them. <b>Thread-safety:</b> stateless apart from its constructor
/// arguments.
/// </para>
/// </summary>
/// <param name="transport">The transport the plaintext is said to come from.</param>
/// <param name="port">The port of the encrypted connection that is published with the marker.</param>
internal sealed class PlaintextPublisherProtocol(PlaintextTransport transport, ushort port) : IProtocol
{
    /// <inheritdoc/>
    public string Name => "plaintext.publisher";

    /// <inheritdoc/>
    public string UiName => "Plaintext Publisher";

    /// <inheritdoc/>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        ProtocolId? someIp = context.Stack?.GetProtocolId(SomeIpProtocol.ProtocolName);
        if (someIp is null)
        {
            return 0;
        }

        PlaintextHandoff.Publish(parentField.Packet.Id, transport, port);
        try
        {
            return parentField.CallProtocol(someIp.Value, data, in context);
        }
        finally
        {
            PlaintextHandoff.Clear();
        }
    }

    /// <summary>
    /// Builds a standard stack with the publisher registered at <paramref name="carrierPort"/> of the UDP or TCP port table. The
    /// SOME/IP configuration is written to a temporary file as in <see cref="SomeIpTestStack"/>. The table of the transport
    /// (<see cref="PlaintextHandoff.TlsPortTableName"/> or the DTLS one) is registered before the standard protocols, as a
    /// decrypting protocol does in its <c>RegisterFields</c>: it is what tells SOME/IP that a decrypting protocol is present,
    /// so SOME/IP binds the listed ports there and reports nothing.
    /// </summary>
    /// <param name="configJson">The SOME/IP service description (lists the TLS and DTLS ports).</param>
    /// <param name="transport">The transport the plaintext is said to come from; also selects the port table (TLS: TCP, DTLS: UDP).</param>
    /// <param name="carrierPort">The port the encrypted traffic of the test runs on; the publisher is registered there.</param>
    /// <param name="settings">Settings preloaded before the protocols register.</param>
    internal static SomeIpTestStack BuildStack(
        string configJson,
        PlaintextTransport transport,
        ushort carrierPort,
        params (string Name, SettingValue Value)[] settings) =>
        SomeIpTestStack.Create(
            configJson,
            settings,
            builder =>
            {
                _ = builder.RegisterProtocol(
                    new PlaintextPublisherProtocol(transport, carrierPort),
                    (b, id, _) => b.RegisterParserInU64TableByName(
                        transport == PlaintextTransport.Tls
                            ? TcpProtocol.PortTableName
                            : UdpProtocol.PortTableName,
                        carrierPort,
                        id));
            },
            prepare: builder =>
            {
                string table = transport == PlaintextTransport.Tls
                    ? PlaintextHandoff.TlsPortTableName
                    : PlaintextHandoff.DtlsPortTableName;
                _ = builder.RegisterProtocolTable(
                    table, "Plaintext Port", Core.Tables.ProtocolTableKeyType.U64, "Plaintext of the test publisher by server port");
            });
}
