// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// An application protocol for tests that only counts how often TCP hands it data and how many bytes each call carried. It
/// consumes everything it is given and appends no fields.
/// </summary>
/// <remarks>
/// Registered on a TCP port without a reassembly configuration it stands for a message-oriented protocol on the plain
/// per-segment path, which is where "this retransmitted segment must not be dispatched again" is observable: the number of
/// calls says how often the protocol saw data. Thread safety: the counters are plain fields; the tests that use them parse
/// on one thread.
/// </remarks>
internal sealed class CountingProbeProtocol : IProtocol
{
    /// <inheritdoc />
    public string Name => "counting.probe";

    /// <inheritdoc />
    public string UiName => "Counting Probe";

    /// <summary>Number of <see cref="Parse"/> calls so far.</summary>
    internal int ParseCount { get; private set; }

    /// <summary>Length of the data of each call, in call order.</summary>
    internal List<int> Lengths { get; } = [];

    /// <inheritdoc />
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        ParseCount++;
        Lengths.Add(data.Length);
        return data.Length;
    }

    /// <summary>
    /// Builds a stack with the probe registered at <paramref name="port"/> of the TCP port table, without a reassembly
    /// configuration, so TCP dispatches every segment to it on its own.
    /// </summary>
    /// <param name="port">TCP port of the probe.</param>
    /// <param name="probe">The probe instance registered in the stack.</param>
    /// <param name="reassembly">Optional reassembly configuration for the probe; without it TCP dispatches every segment on its own.</param>
    internal static Stack BuildStack(ushort port, out CountingProbeProtocol probe, StreamReassemblyConfig? reassembly = null)
    {
#pragma warning disable CA2000 // SettingsManager ownership transfers to Stack via StackBuilder.
        StackBuilder builder = new(new SettingsManager(), new FrameInterfaceRegistry());
#pragma warning restore CA2000
        ProtocolRegistration.RegisterStandardProtocols(builder);
        probe = new();
        CountingProbeProtocol registered = probe;
        builder.RegisterProtocol(registered, (b, id, _) =>
        {
            b.RegisterParserInU64TableByName(TcpProtocol.PortTableName, port, id);
            if (reassembly is not null)
            {
                b.RegisterStreamReassemblyConfig(id, reassembly);
            }
        });
        return builder.Build();
    }
}
