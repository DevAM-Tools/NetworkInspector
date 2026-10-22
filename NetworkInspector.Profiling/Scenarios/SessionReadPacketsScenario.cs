// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// <see cref="Session.ReadPackets(int, Span{Packet?})"/> throughput: reads every packet of a session over mapped PCAPNG
/// IMIX captures in ranges of 64 with recycled packets, as a UI or exporter pages through a capture.
/// </summary>
/// <remarks><b>Thread safety.</b> Not thread-safe; the profiling harness runs one scenario on one thread.</remarks>
internal sealed class SessionReadPacketsScenario : IProfilingScenario
{
    #region Constants

    private const int _TotalPackets = 200_000;
    private const int _PageSize = 64;

    #endregion

    #region Fields

    private readonly bool _Interleaved;
    private readonly Packet?[] _Page = new Packet?[_PageSize];
    private Stack? _Stack;
    private Session? _Session;

    /// <summary>Sum of packet ids, so the JIT cannot drop the reads.</summary>
    private long _Sink;

    #endregion

    #region Constructors

    /// <summary>Creates the scenario.</summary>
    /// <param name="interleaved">Two sources of 100 000 packets each instead of one of 200 000.</param>
    internal SessionReadPacketsScenario(bool interleaved) => _Interleaved = interleaved;

    #endregion

    #region IProfilingScenario

    /// <inheritdoc/>
    public string Name => _Interleaved
        ? "access-session-readpackets-interleaved"
        : "access-session-readpackets";

    /// <inheritdoc/>
    public string Description => _Interleaved
        ? Invariant($"Session.ReadPackets in pages of {_PageSize} over 2 × {_TotalPackets / 2:N0} IMIX packets (mapped PCAPNG).")
        : Invariant($"Session.ReadPackets in pages of {_PageSize} over {_TotalPackets:N0} IMIX packets (mapped PCAPNG).");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => _TotalPackets;

    /// <inheritdoc/>
    public string WorkUnitName => "packets";

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStack();
        int sources = _Interleaved ? 2 : 1;
        int perSource = _TotalPackets / sources;

        // The session owns its sources: Dispose shuts it down and disposes them.
        Session session = new(_Stack);
        for (int s = 0; s < sources; s++)
        {
            string path = CaptureWorkloads.CreateFile(CaptureWorkload.PcapngImix, perSource, _Stack, Invariant($"{Name}-{s}.pcapng"));
            IRandomAccessFrameSource source = CaptureWorkloads.Open(CaptureWorkload.PcapngImix, path, blfCacheBudget: 0);
            if (!session.TryAddFrameSource(source, out _, out SessionFailure? addFailure))
            {
                source.Dispose();
                session.Dispose();
                throw new InvalidOperationException(Invariant($"{Name}: adding a source failed: {addFailure.Value.Message}"));
            }
        }

        if (!session.TryStart(out SessionFailure? startFailure))
        {
            session.Dispose();
            throw new InvalidOperationException(Invariant($"{Name}: session start failed: {startFailure.Value.Message}"));
        }

        // A short ingest would report a too small denominator; fail instead of producing a number.
        _ = session.WaitForCompletion();
        if (session.PacketCount != _TotalPackets)
        {
            int packets = session.PacketCount;
            session.Dispose();
            throw new InvalidOperationException(Invariant($"{Name}: ingested {packets} packets, expected {_TotalPackets}."));
        }

        _Session = session;
    }

    /// <inheritdoc/>
    public void Run()
    {
        // Pages reuse their packets (recycling), so the loop measures reading, not allocation of packet objects.
        Session session = _Session!;
        for (int start = 0; start < _TotalPackets; start += _PageSize)
        {
            int filled = session.ReadPackets(start, _Page);
            for (int i = 0; i < filled; i++)
            {
                if (_Page[i] is not { } packet)
                {
                    throw new InvalidOperationException(Invariant($"Scenario {Name}: packet {start + i} could not be read."));
                }

                _Sink += packet.Id.Value;
            }
        }
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
        _Session?.Dispose();
        _Session = null;
        _Stack?.Dispose();
        _Stack = null;
        Array.Clear(_Page);
        GC.KeepAlive(_Sink);
        SampleCaptureFiles.Cleanup();
    }

    #endregion
}
