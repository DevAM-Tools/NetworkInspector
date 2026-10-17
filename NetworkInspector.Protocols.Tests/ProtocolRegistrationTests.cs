// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// <see cref="ProtocolRegistration.RegisterFairProfilingProtocols"/> must register exactly the lean profiling protocol set,
/// in its documented order.
/// </summary>
internal sealed class ProtocolRegistrationTests
{
    #region Tests

    [Test]
    public async Task RegisterFairProfilingProtocolsRegistersTheComparedSetInOrder()
    {
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());

        IReadOnlyList<SettingsLoadWarning> warnings = builder.RegisterFairProfilingProtocols();
        using Stack stack = builder.Build();

        // The joined names make the assertion order-sensitive: profiling results depend on the registration order.
        await Assert.That(warnings.Count).IsEqualTo(0);
        await Assert.That(_JoinProtocolNames(stack)).IsEqualTo("root,packet,frame,eth,ip,ipv6,udp,tcp");
    }

    [Test]
    public async Task RegisterFairProfilingProtocolsRegistersNoHeuristicTable()
    {
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());

        _ = builder.RegisterFairProfilingProtocols();
        using Stack stack = builder.Build();

        // The lean set has no TCP heuristics; the full registration adds them.
        await Assert.That(stack.GetHeuristicProtocolTableId(TcpProtocol.HeuristicTableName).HasValue).IsFalse();
    }

    #endregion

    #region Helpers

    /// <summary>Joins the registered protocol names in registration order.</summary>
    private static string _JoinProtocolNames(Stack stack)
    {
        ReadOnlySpan<ProtocolInfo> protocols = stack.Protocols.Span;
        string[] names = new string[protocols.Length];
        for (int index = 0; index < protocols.Length; index++)
        {
            names[index] = protocols[index].Name;
        }

        return string.Join(',', names);
    }

    #endregion
}
