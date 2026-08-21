// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests;

/// <summary>
/// Runs a session over a synthetic capture to completion and exposes the single registered
/// listener's <see cref="ListenerId"/>, which is what the listener-bound pull API needs.
///
/// <para>
/// The fixture owns the initial stack because <see cref="Session"/> only takes ownership of stacks
/// it builds itself during <see cref="Session.Restart"/>.
/// </para>
/// </summary>
internal sealed class SessionFixture : IDisposable
{
    #region Fields

    private readonly FrameInterfaceRegistry _Registry = new();
    private readonly Stack _InitialStack;
    private readonly TestFrameSource _Source;
    private readonly TestSessionListener _Listener = new();
    private readonly int _FrameCount;
    private bool _Disposed;

    #endregion

    #region Lifecycle

    /// <summary>
    /// Builds the session, registers the source and one listener, runs the capture to completion
    /// and waits until every packet is visible to pull queries.
    /// </summary>
    private SessionFixture(byte[][] frames, Func<Stack, IFilter?>? filterFactory)
    {
        _FrameCount = frames.Length;
        _InitialStack = TestHarness.CreateStack(_Registry);

        Stack? stackToDispose = _InitialStack;
        try
        {
            _Source = new TestFrameSource(frames);
            Session = new Session(_InitialStack);
            stackToDispose = null;

            if (!Session.TryAddFrameSource(_Source, out _))
            {
                throw new InvalidOperationException("The test frame source was rejected.");
            }

            IFilter? filter = filterFactory?.Invoke(_InitialStack);
            if (!Session.TryAddListener(_Listener, filter, out ListenerInfo? info))
            {
                throw new InvalidOperationException("The test listener was rejected.");
            }

            ListenerId = info.Id;

            Session.TryStart();
            Session.WaitForCompletion();
            WaitHelper.WaitUntil(() => Session.PacketCount >= _FrameCount);
        }
        finally
        {
            stackToDispose?.Dispose();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_Disposed)
        {
            return;
        }

        _Disposed = true;
        Session.Dispose();
        _InitialStack.Dispose();
        _Source.Dispose();
    }

    #endregion

    #region Properties

    /// <summary>The running session.</summary>
    internal Session Session
    {
        get;
    }

    /// <summary>Identifier of the single registered listener.</summary>
    internal ListenerId ListenerId
    {
        get;
    }

    #endregion

    #region Factories

    /// <summary>A capture where every packet is UDP to port 53.</summary>
    internal static SessionFixture WithDnsPorts(int count, string? filterExpression = null)
    {
        byte[][] frames = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            frames[i] = TestHarness.BuildUdpFrame(53, (byte)(64 - i));
        }

        return new SessionFixture(frames, _FilterFactory(filterExpression));
    }

    /// <summary>A UDP capture whose IPv4 TTL values follow <paramref name="timeToLiveValues"/>.</summary>
    internal static SessionFixture WithTtlSequence(byte[] timeToLiveValues, string filterExpression)
    {
        ArgumentNullException.ThrowIfNull(timeToLiveValues);

        byte[][] frames = new byte[timeToLiveValues.Length][];
        for (int i = 0; i < timeToLiveValues.Length; i++)
        {
            frames[i] = TestHarness.BuildUdpFrame(53, timeToLiveValues[i]);
        }

        return new SessionFixture(frames, _FilterFactory(filterExpression));
    }

    /// <summary>A UDP-only capture whose listener filter is produced by <paramref name="filterFactory"/>.</summary>
    internal static SessionFixture WithDnsPortsAndFilter(int count, Func<Stack, IFilter?> filterFactory)
    {
        byte[][] frames = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            frames[i] = TestHarness.BuildUdpFrame(53, (byte)(64 - i));
        }

        return new SessionFixture(frames, filterFactory);
    }

    /// <summary>
    /// A capture where even ids are UDP to port 53 and odd ids are UDP to port 1024, so a
    /// destination-port filter matches exactly half of it.
    /// </summary>
    internal static SessionFixture WithAlternatingPorts(int count, string? filterExpression)
    {
        byte[][] frames = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            frames[i] = TestHarness.BuildUdpFrame(i % 2 == 0 ? (ushort)53 : (ushort)1024);
        }

        return new SessionFixture(frames, _FilterFactory(filterExpression));
    }

    /// <summary>
    /// A capture where even ids are UDP and odd ids are TCP, which lets a protocol filter be
    /// pruned by the presence index before any field is touched.
    /// </summary>
    internal static SessionFixture WithAlternatingProtocols(int count, string? filterExpression)
    {
        byte[][] frames = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            frames[i] = i % 2 == 0 ? TestHarness.BuildUdpFrame(53) : TestHarness.BuildTcpFrame();
        }

        return new SessionFixture(frames, _FilterFactory(filterExpression));
    }

    #endregion

    #region Restart

    /// <summary>Restarts the session onto an equivalent stack, which forces a filter re-bind.</summary>
    internal void Restart()
    {
        Session.Restart(registry => TestHarness.CreateStack(registry));
        WaitHelper.WaitUntil(() => Session.PacketCount >= _FrameCount);
    }

    /// <summary>
    /// Restarts the session onto a stack that knows only Ethernet, so any filter referencing an
    /// IP, UDP or TCP field can no longer be re-bound.
    /// </summary>
    internal void RestartWithEthernetOnlyStack()
    {
        Session.Restart(_BuildEthernetOnlyStack);
        WaitHelper.WaitUntil(() => Session.PacketCount >= _FrameCount);
    }

    #endregion

    #region Private helpers

    /// <summary>
    /// Turns an optional expression into a factory: <see langword="null"/> registers no filter at
    /// all, which is a different slot state than an always-match filter.
    /// </summary>
    private static Func<Stack, IFilter?>? _FilterFactory(string? filterExpression)
    {
        if (filterExpression is null)
        {
            return null;
        }

        return stack => CompileOrThrow(stack, filterExpression);
    }

    /// <summary>Compiles an expression against a stack, throwing when it does not compile.</summary>
    internal static PacketFilter CompileOrThrow(Stack stack, string expression)
    {
        FilterResult<PacketFilter> result = PacketFilter.Compile(expression, stack);
        if (!result.TryGetValue(out PacketFilter? filter))
        {
            throw new InvalidOperationException($"Expected '{expression}' to compile but got {result.Error}.");
        }

        return filter;
    }

    /// <summary>Builds a stack that registers Ethernet only, on the session's own registry.</summary>
    private static Stack _BuildEthernetOnlyStack(FrameInterfaceRegistry registry)
    {
        SettingsManager? settings = new();
        try
        {
            StackBuilder builder = new(settings, registry);
            _ = builder.RegisterProtocol(new EthernetProtocol());
            Stack stack = builder.Build();
            settings = null; // ownership transferred to the stack
            return stack;
        }
        finally
        {
            settings?.Dispose();
        }
    }

    #endregion
}
